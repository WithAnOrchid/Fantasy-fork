#if FANTASY_NET
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Fantasy.Helper;
using Fantasy.Network.Interface;
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

// ReSharper disable GCSuppressFinalizeForTypeWithoutDestructor
#pragma warning disable CS8622 // Nullability of reference types in type of parameter doesn't match the target delegate (possibly because of nullability attributes).
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace Fantasy.Network.TCP
{
    public sealed class TCPServerNetwork : ANetwork
    {
        private Random _random;
        private Socket _socket;
        private SocketAsyncEventArgs _acceptAsync;
        private readonly Dictionary<uint, INetworkChannel> _connectionChannel = new Dictionary<uint, INetworkChannel>();

        /// <summary>
        /// 可选的每连接待发送字节硬上限覆写；为 null 时外网使用 <see cref="ProgramDefine.OuterTcpMaxPendingSendBytes"/>，内网默认不限。
        /// </summary>
        public long? MaxPendingSendBytes { get; set; }

        /// <summary>
        /// 可选的每连接最老未写出包龄硬限覆写（毫秒）；为 null 时外网使用 <see cref="ProgramDefine.OuterTcpMaxPendingSendAgeMs"/>，内网默认不限。
        /// </summary>
        public long? MaxPendingSendAgeMs { get; set; }

        /// <summary>
        /// 可选的已接入连接 Socket 发送缓冲区大小覆写（字节）；为 null 时沿用监听 Socket 继承配置。
        /// </summary>
        public int? AcceptedSocketSendBufferSize { get; set; }

        /// <summary>
        /// 当前活跃连接数。
        /// </summary>
        public int ConnectionCount => _connectionChannel.Count;

        /// <summary>
        /// 拷贝当前活跃的 TCP 服务端通道快照。
        /// </summary>
        public TCPServerNetworkChannel[] CopyChannels()
        {
            var result = new TCPServerNetworkChannel[_connectionChannel.Count];
            var index = 0;

            foreach (var channel in _connectionChannel.Values)
            {
                if (channel is TCPServerNetworkChannel tcpChannel)
                {
                    result[index++] = tcpChannel;
                }
            }

            if (index != result.Length)
            {
                Array.Resize(ref result, index);
            }

            return result;
        }

        internal (long MaxPendingSendBytes, long MaxPendingSendAgeMs) ResolveSendBackpressureLimits()
        {
            var maxBytes = MaxPendingSendBytes ?? (NetworkTarget == NetworkTarget.Outer ? ProgramDefine.OuterTcpMaxPendingSendBytes : 0L);
            var maxAgeMs = MaxPendingSendAgeMs ?? (NetworkTarget == NetworkTarget.Outer ? ProgramDefine.OuterTcpMaxPendingSendAgeMs : 0L);
            return (maxBytes, maxAgeMs);
        }

        public void Initialize(NetworkTarget networkTarget, IPEndPoint address)
        {
            base.Initialize(NetworkType.Server, NetworkProtocolType.TCP, networkTarget, false);
            _random = new Random();
            _acceptAsync = new SocketAsyncEventArgs();
            _socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            }

            _socket.Bind(address);
            _socket.Listen(int.MaxValue);
            _socket.SetSocketBufferToOsLimit();
            Log.Info($"SceneConfigId = {Scene.SceneConfigId} networkTarget = {networkTarget.ToString()} TCPServer Listen {address}");
            _acceptAsync.Completed += OnCompleted;
            AcceptAsync();
        }

        public override void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            try
            {
                foreach (var networkChannel in _connectionChannel.Values.ToArray())
                {
                    networkChannel.Dispose();
                }

                _connectionChannel.Clear();
                _random = null;
                _socket.Dispose();
                _socket = null;
                _acceptAsync.Dispose();
                _acceptAsync = null;
                GC.SuppressFinalize(this);
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

        private void AcceptAsync()
        {
            _acceptAsync.AcceptSocket = null;

            if (_socket.AcceptAsync(_acceptAsync))
            {
                return;
            }
            
            OnAcceptComplete(_acceptAsync);
        }

        private void OnAcceptComplete(SocketAsyncEventArgs asyncEventArgs)
        {
            var acceptSocket = asyncEventArgs.AcceptSocket;
            TCPServerNetworkChannel channel = null;

            try
            {
                if (IsDisposed)
                {
                    return;
                }

                if (asyncEventArgs.SocketError != SocketError.Success)
                {
                    Log.Error($"Socket Accept Error: {asyncEventArgs.SocketError}");
                    return;
                }

                if (acceptSocket == null)
                {
                    return;
                }

                uint channelId;
                do
                {
                    channelId = 0xC0000000 | (uint)_random.Next();
                } while (_connectionChannel.ContainsKey(channelId));

                if (AcceptedSocketSendBufferSize is > 0 and var sendBufferSize)
                {
                    acceptSocket.SendBufferSize = sendBufferSize;
                }

                channel = new TCPServerNetworkChannel(this, acceptSocket, channelId);
                acceptSocket = null;
                _connectionChannel.Add(channelId, channel);
                channel.Start();
            }
            catch (Exception e)
            {
                channel?.Dispose();
                Log.Error(e);
            }
            finally
            {
                acceptSocket?.Dispose();

                var synchronizationContext = Scene?.ThreadSynchronizationContext;

                if (!IsDisposed && synchronizationContext != null)
                {
                    synchronizationContext.Post(() =>
                    {
                        if (!IsDisposed)
                        {
                            AcceptAsync();
                        }
                    });
                }
            }
        }

        public override void RemoveChannel(uint channelId)
        {
            if (IsDisposed || !_connectionChannel.Remove(channelId, out var channel))
            {
                return;
            }

            if (channel.IsDisposed)
            {
                return;
            }

            channel.Dispose();
        }

        #region 网络线程（由Socket底层产生的线程）

        private void OnCompleted(object sender, SocketAsyncEventArgs asyncEventArgs)
        {
            if (asyncEventArgs.LastOperation != SocketAsyncOperation.Accept)
            {
                throw new Exception($"Socket Accept Error: {asyncEventArgs.LastOperation}");
            }

            try
            {
                var synchronizationContext = Scene?.ThreadSynchronizationContext;

                if (synchronizationContext != null)
                {
                    synchronizationContext.Post(() =>
                    {
                        OnAcceptComplete(asyncEventArgs);
                    });

                    return;
                }
            }
            catch (Exception e)
            {
                Log.Error(e);
            }
            
            // Scene 已经销毁或投递失败，由网络线程接管并释放 Socket。
            var acceptSocket = asyncEventArgs.AcceptSocket;
            asyncEventArgs.AcceptSocket = null;
            acceptSocket?.Dispose();
        }

        #endregion
    }
}
#endif


