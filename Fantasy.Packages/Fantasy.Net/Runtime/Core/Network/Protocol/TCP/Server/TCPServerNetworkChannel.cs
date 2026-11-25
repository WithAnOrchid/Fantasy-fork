#if FANTASY_NET
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Fantasy.Async;
using Fantasy.Helper;
using Fantasy.Network.Interface;
using Fantasy.PacketParser;
using Fantasy.Serialize;
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8602 // Dereference of a possibly null reference.

#pragma warning disable CS8604 // Possible null reference argument.
#pragma warning disable CS8622 // Nullability of reference types in type of parameter doesn't match the target delegate (possibly because of nullability attributes).

namespace Fantasy.Network.TCP
{
    public sealed class TCPServerNetworkChannel : ANetworkServerChannel
    {
        private readonly struct PendingSendEntry
        {
            public readonly MemoryStreamBuffer Buffer;
            public readonly int ByteCount;
            public readonly long EnqueuedAtMs;

            public PendingSendEntry(MemoryStreamBuffer buffer, int byteCount, long enqueuedAtMs)
            {
                Buffer = buffer;
                ByteCount = byteCount;
                EnqueuedAtMs = enqueuedAtMs;
            }
        }

        private bool _isSending;
        private volatile bool _isInnerDispose;
        private long _pendingSendBytes;
        private int _inFlightBytes;
        private long _inFlightEnqueuedAtMs;
        private long _maxPendingSendBytes;
        private long _maxPendingSendAgeMs;
        private readonly Socket _socket;
        private readonly ANetwork _network;
        private readonly Pipe _pipe = new Pipe();
        private readonly SocketAsyncEventArgs _sendArgs;
        private readonly ReadOnlyMemoryPacketParser _packetParser;
        private readonly Queue<PendingSendEntry> _sendBuffers = new Queue<PendingSendEntry>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// 当前在途与排队待发送的总字节数。
        /// </summary>
        public long PendingSendBytes => _pendingSendBytes;

        /// <summary>
        /// 当前在途与排队待发送的数据包数量。
        /// </summary>
        public int PendingSendCount => _sendBuffers.Count + (_inFlightBytes > 0 ? 1 : 0);

        /// <summary>
        /// 最老未写出数据包的入队时间戳（毫秒）；无待发送数据时为 0。
        /// </summary>
        public long OldestPendingSendEnqueueTimeMs
        {
            get
            {
                if (_inFlightEnqueuedAtMs > 0)
                {
                    return _inFlightEnqueuedAtMs;
                }

                return _sendBuffers.TryPeek(out var head) ? head.EnqueuedAtMs : 0;
            }
        }

        /// <summary>
        /// 当前通道生效的待发送字节硬上限；小于等于 0 表示不限。
        /// </summary>
        public long MaxPendingSendBytes => _maxPendingSendBytes;

        /// <summary>
        /// 当前通道生效的最老未写出包龄硬限（毫秒）；小于等于 0 表示不限。
        /// </summary>
        public long MaxPendingSendAgeMs => _maxPendingSendAgeMs;

        public TCPServerNetworkChannel(ANetwork network, Socket socket, uint id) : base(network, id, socket.RemoteEndPoint)
        {
            _socket = socket;
            _network = network;

            if (network is TCPServerNetwork tcpServerNetwork)
            {
                (_maxPendingSendBytes, _maxPendingSendAgeMs) = tcpServerNetwork.ResolveSendBackpressureLimits();
            }
            else if (network.NetworkTarget == NetworkTarget.Outer)
            {
                _maxPendingSendBytes = ProgramDefine.OuterTcpMaxPendingSendBytes;
                _maxPendingSendAgeMs = ProgramDefine.OuterTcpMaxPendingSendAgeMs;
            }

            try
            {
                _socket.NoDelay = true;
                _sendArgs = new SocketAsyncEventArgs();
                _sendArgs.Completed += OnSendCompletedHandler;
                _packetParser = PacketParserFactory.CreateReadOnlyMemoryPacketParser(network);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// 更新当前通道的待发送字节上限与最老未写出包龄硬限（小于等于 0 表示不限）。
        /// </summary>
        public void ConfigureSendLimits(long maxPendingSendBytes, long maxPendingSendAgeMs)
        {
            _maxPendingSendBytes = maxPendingSendBytes;
            _maxPendingSendAgeMs = maxPendingSendAgeMs;
        }
        
        internal void Start()
        {
            ReadPipeDataAsync().Coroutine();
            ReceiveSocketAsync().Coroutine();
        }

        public override void Dispose()
        {
            if (IsDisposed || _isInnerDispose)
            {
                return;
            }

            _isInnerDispose = true;
            _network.RemoveChannel(Id);
            
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    _cancellationTokenSource.Cancel();
                }
                catch (OperationCanceledException)
                {
                    // 通常情况下，此处的异常可以忽略
                }
            }

            try
            {
                if (_socket != null)
                {
                    try
                    {
                        _socket.Shutdown(SocketShutdown.Both);
                    }
                    catch (SocketException)
                    {
                        // Socket 可能已被远端关闭或处于不可 shutdown 状态，释放流程继续
                    }
                    catch (ObjectDisposedException)
                    {
                        // Socket 已被并发释放，释放流程继续
                    }
                    
                    try
                    {
                        _socket.Close();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e);
                    }
                }
                
                _sendArgs?.Dispose();
                ClearSendBuffers();
                _packetParser?.Dispose();
                _isSending = false;
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            finally
            {
                base.Dispose();
            }
        }

        #region ReceiveSocket

        private async FTask ReceiveSocketAsync()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    var memory = _pipe.Writer.GetMemory(8192);
                    var count = await _socket.ReceiveAsync(memory, SocketFlags.None, _cancellationTokenSource.Token);
                    
                    if (count == 0)
                    {
                        Dispose();
                        break;
                    }
                    
                    _pipe.Writer.Advance(count);
                    await _pipe.Writer.FlushAsync();
                }
                catch (SocketException)
                {
                    Dispose();
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"Unexpected exception: {ex.Message}");
                    Dispose();
                    break;
                }
            }

            await _pipe.Writer.CompleteAsync();
        }

        #endregion

        #region ReceivePipeData

        private async FTask ReadPipeDataAsync()
        {
            var pipeReader = _pipe.Reader;
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                ReadResult result = default;

                try
                {
                    result = await pipeReader.ReadAsync(_cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // 出现这个异常表示取消了_cancellationTokenSource。一般Channel断开会取消。
                    break;
                }

                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                while (TryReadMessage(ref buffer, out var message))
                {
                    ReceiveData(ref message);
                    consumed = buffer.Start;
                }

                if (result.IsCompleted)
                {
                    break;
                }

                pipeReader.AdvanceTo(consumed, examined);
            }

            await pipeReader.CompleteAsync();
        }

        private bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> message)
        {
            if (buffer.Length == 0)
            {
                message = default;
                return false;
            }

            message = buffer.First;

            if (message.Length == 0)
            {
                message = default;
                return false;
            }

            buffer = buffer.Slice(message.Length);
            return true;
        }

        private void ReceiveData(ref ReadOnlyMemory<byte> buffer)
        {
            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested && 
                       _packetParser.UnPack(ref buffer, out var packInfo))
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        packInfo.Dispose();
                        return;
                    }

                    Session.Receive(packInfo);
                }
            }
            catch (ScanException e)
            {
                Log.Warning($"RemoteAddress:{RemoteEndPoint} \n{e}");
                Dispose();
            }
            catch (Exception e)
            {
                Log.Error($"RemoteAddress:{RemoteEndPoint} \n{e}");
                Dispose();
            }
        }

        #endregion

        #region Send

        internal bool CheckSendBackpressure(long nowMs)
        {
            if (IsDisposed || _isInnerDispose)
            {
                return false;
            }

            if (!ExceedsSendBackpressure(_pendingSendBytes, nowMs, out var reason, out var oldestAgeMs))
            {
                return false;
            }

            Log.Warning(
                $"TCPServerNetworkChannel backpressure limit exceeded ({reason}). " +
                $"ChannelId:{Id} RemoteEndPoint:{RemoteEndPoint} " +
                $"PendingBytes:{_pendingSendBytes} MaxBytes:{_maxPendingSendBytes} " +
                $"OldestAgeMs:{oldestAgeMs} MaxAgeMs:{_maxPendingSendAgeMs} PendingCount:{PendingSendCount}");
            Dispose();
            return true;
        }

        private bool ExceedsSendBackpressure(long candidatePendingBytes, long nowMs, out string reason, out long oldestAgeMs)
        {
            var oldestEnqueuedAtMs = OldestPendingSendEnqueueTimeMs;
            oldestAgeMs = oldestEnqueuedAtMs > 0 ? Math.Max(0, nowMs - oldestEnqueuedAtMs) : 0;

            if (_maxPendingSendBytes > 0 && candidatePendingBytes > _maxPendingSendBytes)
            {
                reason = "pending-bytes";
                return true;
            }

            if (_maxPendingSendAgeMs > 0 && oldestEnqueuedAtMs > 0 && oldestAgeMs >= _maxPendingSendAgeMs)
            {
                reason = "pending-age";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private void CompleteInFlightSend(MemoryStreamBuffer memoryStreamBuffer)
        {
            if (!_isInnerDispose)
            {
                _pendingSendBytes = Math.Max(0, _pendingSendBytes - _inFlightBytes);
                _inFlightBytes = 0;
                _inFlightEnqueuedAtMs = 0;
            }

            ReturnMemoryStream(memoryStreamBuffer);
        }

        public override void Send(uint rpcId, long address, MemoryStreamBuffer memoryStream, IMessage message, Type messageType)
        {
            if (IsDisposed || _isInnerDispose)
            {
                message?.Dispose();
                return;
            }

            var packed = _packetParser.Pack(ref rpcId, ref address, memoryStream, message, messageType);
            var packetBytes = Math.Max(0, (int)packed.Position);
            var now = TimeHelper.Now;
            var candidatePendingBytes = _pendingSendBytes + packetBytes;

            if (ExceedsSendBackpressure(candidatePendingBytes, now, out var reason, out var oldestAgeMs))
            {
                ReturnMemoryStream(packed);
                Log.Warning(
                    $"TCPServerNetworkChannel backpressure limit exceeded ({reason}). " +
                    $"ChannelId:{Id} RemoteEndPoint:{RemoteEndPoint} " +
                    $"PendingBytes:{candidatePendingBytes} MaxBytes:{_maxPendingSendBytes} " +
                    $"OldestAgeMs:{oldestAgeMs} MaxAgeMs:{_maxPendingSendAgeMs} PendingCount:{PendingSendCount + 1}");
                Dispose();
                return;
            }

            _sendBuffers.Enqueue(new PendingSendEntry(packed, packetBytes, now));
            _pendingSendBytes = candidatePendingBytes;

            if (!_isSending)
            {
                Send();
            }
        }
        
        private void Send()
        {
            if (_isSending || IsDisposed || _isInnerDispose)
            {
                return;
            }
            
            _isSending = true;

            while (_sendBuffers.TryDequeue(out var entry))
            {
                var memoryStreamBuffer = entry.Buffer;
                _inFlightBytes = entry.ByteCount;
                _inFlightEnqueuedAtMs = entry.EnqueuedAtMs;

                var offset = 0;
                var totalLength = (int)memoryStreamBuffer.Position;
                var buffer = memoryStreamBuffer.GetBuffer();

                while (offset < totalLength)
                {
                    _sendArgs.UserToken = memoryStreamBuffer;
                    _sendArgs.SetBuffer(buffer, offset, totalLength - offset);

                    try
                    {
                        if (_socket.SendAsync(_sendArgs))
                        {
                            return;
                        }
                        
                        if (_sendArgs.SocketError != SocketError.Success)
                        {
                            CompleteInFlightSend(memoryStreamBuffer);
                            _isSending = false;
                            Dispose();
                            return;
                        }
                        
                        var sent = _sendArgs.BytesTransferred;
                        if (sent == 0)
                        {
                            CompleteInFlightSend(memoryStreamBuffer);
                            _isSending = false;
                            Dispose();
                            return;
                        }
                        
                        offset += sent;
                    }
                    catch
                    {
                        CompleteInFlightSend(memoryStreamBuffer);
                        _isSending = false;
                        Dispose();
                        return;
                    }
                }
                
                // 同步发送完整后归还 buffer
                CompleteInFlightSend(memoryStreamBuffer);
            }
            
            _isSending = false;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReturnMemoryStream(MemoryStreamBuffer memoryStream)
        {
            if ((memoryStream.MemoryStreamBufferSource & MemoryStreamBufferSource.Return) == 0)
            {
                return;
            }

            if (_network.IsDisposed)
            {
                memoryStream.Dispose();
                return;
            }

            _network.MemoryStreamBufferPool.ReturnMemoryStream(memoryStream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DisposeMemoryStream(MemoryStreamBuffer memoryStream)
        {
            if ((memoryStream.MemoryStreamBufferSource & MemoryStreamBufferSource.Return) == 0)
            {
                return;
            }

            memoryStream.Dispose();
        }
        
        private void ClearSendBuffers()
        {
            while (_sendBuffers.TryDequeue(out var entry))
            {
                ReturnMemoryStream(entry.Buffer);
            }

            _pendingSendBytes = 0;
            _inFlightBytes = 0;
            _inFlightEnqueuedAtMs = 0;
        }
        
        private void OnSendCompletedHandler(object sender, SocketAsyncEventArgs asyncEventArgs)
        {
            var memoryStreamBuffer = (MemoryStreamBuffer)asyncEventArgs.UserToken;
            var synchronizationContext = Scene?.ThreadSynchronizationContext;
            
            if (_isInnerDispose || synchronizationContext == null)
            {
                DisposeMemoryStream(memoryStreamBuffer);
                return;
            }
            
            // 限制最大重试次数，防止死循环
            // 理论上一个包不应该需要超过 10000 次 partial send
            const int maxRetries = 10000;

            for (var i = 0; i < maxRetries; i++)
            {
                if (_isInnerDispose)
                {
                    DisposeMemoryStream(memoryStreamBuffer);
                    return;
                }

                if (asyncEventArgs.SocketError != SocketError.Success || asyncEventArgs.BytesTransferred == 0)
                {
                    synchronizationContext.Post(() =>
                    {
                        _isSending = false;
                        CompleteInFlightSend(memoryStreamBuffer);
                        Dispose();
                    });
                
                    return;
                }
                
                var sent = asyncEventArgs.BytesTransferred;
                var total = asyncEventArgs.Count;
                
                if (sent < total)
                {
                    // 部分发送，更新 offset 继续发送剩余部分
                    var newOffset = asyncEventArgs.Offset + sent;
                    var remaining = total - sent;
                
                    try
                    {
                        asyncEventArgs.SetBuffer(newOffset, remaining);
                        
                        if (_socket.SendAsync(asyncEventArgs))
                        {
                            return;  // 继续异步发送，等待下次回调
                        }
            
                        continue;
                    }
                    catch
                    {
                        synchronizationContext.Post(() =>
                        {
                            _isSending = false;
                            CompleteInFlightSend(memoryStreamBuffer);
                            Dispose();
                        });
                        return;
                    }
                }
                
                // 当前 buffer 发送完整，归还并继续下一个
                synchronizationContext.Post(() =>
                {
                    CompleteInFlightSend(memoryStreamBuffer);

                    if (IsDisposed || _isInnerDispose)
                    {
                        _isSending = false;
                        return;
                    }

                    if (CheckSendBackpressure(TimeHelper.Now))
                    {
                        _isSending = false;
                        return;
                    }
                
                    if (_sendBuffers.Count > 0)
                    {
                        _isSending = false;
                        Send();
                    }
                    else
                    {
                        _isSending = false;
                    }
                });
                
                return;
            }
            
            // 如果达到最大重试次数，记录错误并断开连接
            Log.Error($"OnSendCompleted exceeded max retries ({maxRetries}), possible infinite loop. Disconnecting.");
            
            synchronizationContext.Post(() =>
            {
                CompleteInFlightSend(memoryStreamBuffer);
                _isSending = false;
                Dispose();
            });
        }

        #endregion
    }
}
#endif
