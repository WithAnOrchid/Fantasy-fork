using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Fantasy.Assembly;
using Fantasy.Async;
using Fantasy.DataStructure.Dictionary;
using Fantasy.Entitas;
using Fantasy.InnerMessage;
// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable InvertIf
// ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
// ReSharper disable CheckNamespace
#pragma warning disable CS8601 // Possible null reference assignment.
#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
#pragma warning disable CS8602 // Dereference of a possibly null reference.
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

namespace Fantasy.Network.Interface
{
    public sealed class MessageDispatcherComponent : Entity, IAssemblyLifecycle
    {
        private CoroutineLock _receiveRouteMessageLock;
        private UInt32FrozenDictionary<Type> _opCodeDictionary;
        private UInt32FrozenDictionary<Type> _responseTypeDictionary;
        private UInt32FrozenDictionary<Func<Session, uint, object, FTask>> _messageHandlerDictionary;
        
        private readonly UInt32MergerFrozenDictionary<Type> _opCodeMerger = new();
        private readonly UInt32MergerFrozenDictionary<Type> _responseTypeMerger = new();
        private readonly UInt32MergerFrozenDictionary<Func<Session, uint, object, FTask>> _messageHandlerMerger = new();
#if FANTASY_NET
        private UInt32FrozenDictionary<int> _customRouteDictionary;
        private UInt32FrozenDictionary<Func<Session, Entity, uint, object, FTask>> _routeMessageHandlerDictionary;
        private readonly UInt32MergerFrozenDictionary<int> _customRouteMerger = new();
        private readonly UInt32MergerFrozenDictionary<Func<Session, Entity, uint, object, FTask>> _routeMessageHandlerMerger = new();
#endif
        public override void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }
            
            _receiveRouteMessageLock.Dispose();
            _opCodeDictionary = null;
            _responseTypeDictionary = null;
            _messageHandlerDictionary = null;
#if FANTASY_NET
            _customRouteDictionary = null;
            _routeMessageHandlerDictionary = null;
#endif
            AssemblyLifecycle.Remove(this);
            base.Dispose();
        }

        #region AssemblyManifest

        internal async FTask<MessageDispatcherComponent> Initialize()
        {
            _receiveRouteMessageLock = Scene.CoroutineLockComponent.Create(GetType().TypeHandle.Value.ToInt64());
            await AssemblyLifecycle.Add(this);
            return this;
        }

        public Task OnLoad(AssemblyManifest assemblyManifest)
        {
            var assemblyManifestId = assemblyManifest.AssemblyManifestId;

            return AssemblyLifecycle.RunOnContext(
                Scene.ThreadSynchronizationContext,
                () =>
                {
                    // 注册Handler
                    var messageHandlerResolver = assemblyManifest.MessageHandlerResolver;
                    _messageHandlerMerger.Add(
                        assemblyManifestId,
                        messageHandlerResolver.MessageHandlerOpCodes(),
                        messageHandlerResolver.MessageHandlers());
                    _messageHandlerDictionary = _messageHandlerMerger.GetFrozenDictionary();

                    // 注册OpCode
                    var opCodeResolver = assemblyManifest.OpCodeRegistrar;
                    _opCodeMerger.Add(
                        assemblyManifestId,
                        opCodeResolver.TypeOpCodes(),
                        opCodeResolver.OpCodeTypes());
                    _opCodeDictionary = _opCodeMerger.GetFrozenDictionary();
#if FANTASY_NET
                    _customRouteMerger.Add(
                        assemblyManifestId,
                        opCodeResolver.CustomRouteTypeOpCodes(),
                        opCodeResolver.CustomRouteTypes());
                    _customRouteDictionary = _customRouteMerger.GetFrozenDictionary();
            
                    _routeMessageHandlerMerger.Add(
                        assemblyManifestId,
                        messageHandlerResolver.AddressMessageHandlerOpCodes(),
                        messageHandlerResolver.AddressMessageHandler());
                    _routeMessageHandlerDictionary =
                        _routeMessageHandlerMerger.GetFrozenDictionary();
#endif
                    // 注册ResponseType
                    var responseTypeRegistrar = assemblyManifest.ResponseTypeRegistrar;
                    _responseTypeMerger.Add(
                        assemblyManifestId,
                        responseTypeRegistrar.OpCodes(),
                        responseTypeRegistrar.Types());
                    _responseTypeDictionary = _responseTypeMerger.GetFrozenDictionary();
                });
        }

        public Task OnUnload(AssemblyManifest assemblyManifest)
        {
            var assemblyManifestId = assemblyManifest.AssemblyManifestId;

            return AssemblyLifecycle.RunOnContext(
                Scene.ThreadSynchronizationContext,
                () =>
                {
                    _opCodeMerger.Remove(assemblyManifestId);
                    _responseTypeMerger.Remove(assemblyManifestId);
                    _messageHandlerMerger.Remove(assemblyManifestId);

                    _opCodeDictionary = _opCodeMerger.GetFrozenDictionary();
                    _responseTypeDictionary = _responseTypeMerger.GetFrozenDictionary();
                    _messageHandlerDictionary =
                        _messageHandlerMerger.GetFrozenDictionary();
#if FANTASY_NET
                    _customRouteMerger.Remove(assemblyManifestId);
                    _routeMessageHandlerMerger.Remove(assemblyManifestId);
        
                    _customRouteDictionary = _customRouteMerger.GetFrozenDictionary();
                    _routeMessageHandlerDictionary =
                        _routeMessageHandlerMerger.GetFrozenDictionary();
#endif
                });
        }

        #endregion

        internal void MessageHandler(Session session, Type type, object message, uint rpcId, uint protocolCode)
        {
            if (!_messageHandlerDictionary.TryGetValue(protocolCode, out var messageHandler))
            {
                ((IMessage)message).Dispose();
                Log.Warning($"Scene:{session.Scene.Id} Found Unhandled Message: {type.FullName}");
                return;
            }

            messageHandler(session, rpcId, message).Coroutine();
        }
#if FANTASY_NET
        /// <summary>
        /// 查询指定协议号的路由消息是否已注册处理器，只做查询，不交出处理器表。
        /// </summary>
        /// <param name="protocolCode">路由消息的协议号</param>
        /// <returns>已注册处理器返回 true，否则返回 false</returns>
        public bool HasAddressMessageHandler(uint protocolCode)
        {
            return _routeMessageHandlerDictionary != null &&
                   _routeMessageHandlerDictionary.TryGetValue(protocolCode, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async FTask<bool> InnerAddressMessageHandler(Session session, Entity entity, uint rpcId, uint protocolCode, object message)
        {
            if (!_routeMessageHandlerDictionary.TryGetValue(protocolCode, out var messageHandler))
            {
                var isRequest = rpcId != 0 && message is IAddressRequest;

                ((IMessage)message).Dispose();
                
                if (isRequest)
                {
                    FailRouteResponse(
                        session,
                        protocolCode,
                        InnerErrorCode.ErrRpcFail,
                        rpcId);
                }
                
                return false;
            }

            var interceptor = _addressMessageInterceptor;

            if (interceptor != null)
            {
                await InterceptAddressMessage(interceptor, messageHandler, session, entity, rpcId, protocolCode, message);
                return true;
            }

            await messageHandler(session, entity, rpcId, message);
            return true;
        }

        internal async FTask AddressMessageHandler(Session session, Type type, Entity entity, object message, uint rpcId, uint protocolCode)
        {
            if (entity is Scene)
            {
                // 如果是Scene的话、就不要加锁了、如果加锁很一不小心就可能会造成死锁
                if (!await InnerAddressMessageHandler(session, entity, rpcId, protocolCode, message))
                {
                    Log.Warning($"Scene:{session.Scene.Id} Found Unhandled AddressMessage: {type}");
                }
                return;
            }
            
            // 使用协程锁来确保消息的顺序
            var runtimeId = entity.RuntimeId;
            var sessionRuntimeId = session.RuntimeId;
            using (await _receiveRouteMessageLock.Wait(runtimeId))
            {
                if (sessionRuntimeId != session.RuntimeId)
                {
                    ((IMessage)message).Dispose();
                    return;
                }

                if (runtimeId != entity.RuntimeId)
                {
                    uint? requestOpCode = message is IAddressRequest request
                        ? request.OpCode()
                        : null;

                    ((IMessage)message).Dispose();

                    if (requestOpCode.HasValue)
                    {
                        FailRouteResponse(session, requestOpCode.Value, InnerErrorCode.ErrEntityNotFound, rpcId);
                    }
                    
                    return;
                }

                if (!await InnerAddressMessageHandler(session, entity, rpcId, protocolCode, message))
                {
                    Log.Warning($"Scene:{session.Scene.Id} Found Unhandled AddressMessage: {message.GetType()}");
                }
            }
        }

        #region AddressMessageInterceptor

        private IAddressMessageInterceptor _addressMessageInterceptor;

        /// <summary>
        /// 装上路由报文拦截器。装上后，这个分发组件收到的每条有处理器的路由报文，交给处理器之前先问它一次
        /// （<see cref="IAddressMessageInterceptor.Arrive"/>），每条问过的报文恰好通知它一次结局
        /// （<see cref="IAddressMessageInterceptor.Settled"/>）。没装时分发路径不变。
        /// 一个分发组件同时只装一个；同一根场景下的子场景共用根的分发组件，装在根上就管整个根。
        /// 只在分发组件所属 Scene 的线程上调用。
        /// </summary>
        /// <param name="interceptor">要装上的拦截器</param>
        /// <exception cref="ArgumentNullException">拦截器为空</exception>
        /// <exception cref="InvalidOperationException">已经装着一个拦截器</exception>
        public void InstallAddressMessageInterceptor(IAddressMessageInterceptor interceptor)
        {
            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (_addressMessageInterceptor != null)
            {
                throw new InvalidOperationException("An address message interceptor is already installed on this dispatcher.");
            }

            _addressMessageInterceptor = interceptor;
        }

        /// <summary>
        /// 拆下路由报文拦截器，必须是装上的那一个。拆下之前已经问过的报文照常走完，结局仍通知它。
        /// 只在分发组件所属 Scene 的线程上调用。
        /// </summary>
        /// <param name="interceptor">装上的那个拦截器</param>
        /// <exception cref="ArgumentNullException">拦截器为空</exception>
        /// <exception cref="InvalidOperationException">装着的不是这个拦截器</exception>
        public void RemoveAddressMessageInterceptor(IAddressMessageInterceptor interceptor)
        {
            if (interceptor == null)
            {
                throw new ArgumentNullException(nameof(interceptor));
            }

            if (!ReferenceEquals(_addressMessageInterceptor, interceptor))
            {
                throw new InvalidOperationException("The interceptor to remove is not the one installed on this dispatcher.");
            }

            _addressMessageInterceptor = null;
        }

        private async FTask InterceptAddressMessage(IAddressMessageInterceptor interceptor, Func<Session, Entity, uint, object, FTask> messageHandler, Session session, Entity entity, uint rpcId, uint protocolCode, object message)
        {
            // 到达时记下会话与目标的运行时编号，等待之后拿来重核
            var sessionRuntimeId = session.RuntimeId;
            var entityRuntimeId = entity.RuntimeId;
            // 请求的答复类型也在到达时取好：等待中接收方根场景关掉会释放这个分发组件，答复类型表随之置空，
            // 之后作答只用这里取好的类型
            var responseType = message is IAddressRequest request ? RouteResponseType(request.OpCode()) : null;
            var outcome = AddressMessageOutcome.Faulted;
            // 到达时抛出的没有回答，结局里的标记为空
            var verdict = default(AddressMessageVerdict);

            try
            {
                uint error;

                try
                {
                    verdict = interceptor.Arrive(session, entity, rpcId, protocolCode, message);
                    error = verdict.Wait == null ? verdict.Error : await verdict.Wait;
                }
                catch (Exception e)
                {
                    // 拦截器到达时抛出，或等待的任务失败：释放报文，请求答 ErrRpcFail
                    AnswerAddressMessage(session, rpcId, message, InnerErrorCode.ErrRpcFail, responseType);
                    Log.Error(e);
                    return;
                }

                if (!AddressMessageStillDeliverable(session, entity, rpcId, message, sessionRuntimeId, entityRuntimeId, !(entity is Scene), responseType))
                {
                    outcome = AddressMessageOutcome.Dropped;
                    return;
                }

                if (error != 0)
                {
                    AnswerAddressMessage(session, rpcId, message, error, responseType);
                    outcome = AddressMessageOutcome.Answered;
                    return;
                }

                // 原处理器抛出时结局留在 Faulted，异常照原样往上抛
                await messageHandler(session, entity, rpcId, message);
                outcome = AddressMessageOutcome.Handled;
            }
            finally
            {
                SettleAddressMessage(interceptor, session, entity, protocolCode, outcome, verdict.Token);
            }
        }

        // 与上面出锁后的核对一致：先核会话（只释放、不答），再核目标（释放，请求答 ErrEntityNotFound）。
        // checkSession 为 false 时不核会话：发到 Scene 的报文。responseType 是到达时取好的答复类型，只有请求才有。
        private static bool AddressMessageStillDeliverable(Session session, Entity entity, uint rpcId, object message, long sessionRuntimeId, long entityRuntimeId, bool checkSession, Type? responseType)
        {
            if (checkSession && sessionRuntimeId != session.RuntimeId)
            {
                ((IMessage)message).Dispose();
                return false;
            }

            if (entityRuntimeId != entity.RuntimeId)
            {
                ((IMessage)message).Dispose();

                if (responseType != null)
                {
                    SendAddressFailure(session, responseType, InnerErrorCode.ErrEntityNotFound, rpcId);
                }

                return false;
            }

            return true;
        }

        // 与上面找不到处理器时的作答一致：释放报文，是请求就答这个错误码（responseType 只有请求才有）。
        private static void AnswerAddressMessage(Session session, uint rpcId, object message, uint error, Type? responseType)
        {
            ((IMessage)message).Dispose();

            if (rpcId != 0 && responseType != null)
            {
                SendAddressFailure(session, responseType, error, rpcId);
            }
        }

        // 与下面 CreateRouteResponse 的选法一致：登记了答复类型的用它，没登记的用 AddressResponse。
        private Type RouteResponseType(uint requestOpCode)
        {
            return _responseTypeDictionary.TryGetValue(requestOpCode, out var responseType)
                ? responseType
                : typeof(AddressResponse);
        }

        // 发出的答复与下面 FailRouteResponse 的相同；答复类型用到达时取好的，不再查这个分发组件的表。
        private static void SendAddressFailure(Session session, Type responseType, uint error, uint rpcId)
        {
            var response = (IAddressResponse)Activator.CreateInstance(responseType);
            response.ErrorCode = error;
            session.Send(response, responseType, rpcId);
        }

        private static void SettleAddressMessage(IAddressMessageInterceptor interceptor, Session session, Entity entity, uint protocolCode, AddressMessageOutcome outcome, object token)
        {
            try
            {
                interceptor.Settled(session, entity, protocolCode, outcome, token);
            }
            catch (Exception e)
            {
                // 结局已定：只记错误，不再作答、不再释放
                Log.Error(e);
            }
        }

        #endregion
#endif
        #region Response

        internal void FailRouteResponse(Session session, uint requestOpCode, uint error, uint rpcId)
        {
            session.Send(CreateRouteResponse(requestOpCode, error, out var responseType), responseType, rpcId);
        }

        internal IResponse CreateResponse(uint requestOpCode, uint error)
        {
            if (_responseTypeDictionary.TryGetValue(requestOpCode, out var responseType))
            {
                var findResponse = (IResponse)Activator.CreateInstance(responseType);
                findResponse.ErrorCode = error;
                return findResponse;
            }

            var response = new Response();
            response.ErrorCode = error;
            return response;
        }

        private IAddressResponse CreateRouteResponse(uint requestOpCode, uint error, out Type responseType)
        {
            if (_responseTypeDictionary.TryGetValue(requestOpCode, out responseType))
            {
                var findResponse = (IAddressResponse)Activator.CreateInstance(responseType);
                findResponse.ErrorCode = error;
                return findResponse;
            }
            
            responseType = typeof(AddressResponse);
            var response = new AddressResponse();
            response.ErrorCode = error;
            return response;
        }

        #endregion

        #region OpCode

        internal Type? GetOpCodeType(uint opCode)
        {
            return _opCodeDictionary.TryGetValue(opCode, out var type) ? type : null;
        }
#if FANTASY_NET
        internal int? GetCustomRouteType(uint protocolCode)
        {
            return _customRouteDictionary.TryGetValue(protocolCode, out var type) ? type : null;
        }
#endif
        #endregion
    }
#if FANTASY_NET
    /// <summary>
    /// 路由报文拦截器，装在分发组件上（<see cref="MessageDispatcherComponent.InstallAddressMessageInterceptor"/>）。
    /// 分发组件找到报文的处理器之后、交给它之前问 <see cref="Arrive"/>；每条问过的报文恰好通知一次 <see cref="Settled"/>。
    /// 两个方法都在分发组件所属 Scene 的线程上同步调用；发到实体的报文在接收锁内调用。
    /// </summary>
    public interface IAddressMessageInterceptor
    {
        /// <summary>
        /// 一条有处理器的路由报文到达，回答放行、以错误码作答或等一个任务再定。
        /// 分发组件拿到回答（要等的等完）之后重核：发到实体的报文核会话与目标，发到 Scene 的报文只核目标；没过的不再往下，结局为 <see cref="AddressMessageOutcome.Dropped"/>。
        /// 这里抛出时报文释放、请求答 ErrRpcFail，结局为 <see cref="AddressMessageOutcome.Faulted"/>。
        /// </summary>
        AddressMessageVerdict Arrive(Session session, Entity entity, uint rpcId, uint protocolCode, object message);

        /// <summary>
        /// 报文的结局。<paramref name="token"/> 是到达时回答里带的标记，原样交回；到达时抛出的为 null。
        /// 这里抛出时只记错误，结局不变，报文不再作答、不再释放。
        /// </summary>
        void Settled(Session session, Entity entity, uint protocolCode, AddressMessageOutcome outcome, object token);
    }

    /// <summary>
    /// 拦截器对一条到达报文的回答，三种之一：放行、以错误码作答、等一个任务再定。
    /// 都可以带一个不透明的标记，结局通知时原样交回；不带标记的放行不分配。
    /// </summary>
    public readonly struct AddressMessageVerdict
    {
        private AddressMessageVerdict(uint error, FTask<uint> wait, object token)
        {
            Error = error;
            Wait = wait;
            Token = token;
        }

        internal uint Error { get; }

        internal FTask<uint> Wait { get; }

        internal object Token { get; }

        /// <summary>
        /// 放行：交给原处理器。
        /// </summary>
        /// <param name="token">结局通知时原样交回的标记</param>
        public static AddressMessageVerdict Proceed(object token = null)
        {
            return new AddressMessageVerdict(0, null, token);
        }

        /// <summary>
        /// 以错误码作答：不交给原处理器，释放报文，是请求就答这个码。
        /// </summary>
        /// <param name="error">答复的错误码，不能为 0</param>
        /// <param name="token">结局通知时原样交回的标记</param>
        /// <exception cref="ArgumentOutOfRangeException">错误码为 0</exception>
        public static AddressMessageVerdict AnswerWith(uint error, object token = null)
        {
            if (error == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(error), error, "An answer needs a non-zero error code.");
            }

            return new AddressMessageVerdict(error, null, token);
        }

        /// <summary>
        /// 等这个任务完成再定：结果为 0 时放行，不为 0 时以这个码作答；任务失败时报文释放、请求答 ErrRpcFail。
        /// 等待期间发到实体的报文仍占着接收锁。
        /// </summary>
        /// <param name="wait">在分发组件所属 Scene 的线程上完成的任务</param>
        /// <param name="token">结局通知时原样交回的标记</param>
        /// <exception cref="ArgumentNullException">任务为空</exception>
        public static AddressMessageVerdict WaitFor(FTask<uint> wait, object token = null)
        {
            if (wait == null)
            {
                throw new ArgumentNullException(nameof(wait));
            }

            return new AddressMessageVerdict(0, wait, token);
        }
    }

    /// <summary>
    /// 一条问过拦截器的路由报文的结局。
    /// </summary>
    public enum AddressMessageOutcome
    {
        /// <summary>
        /// 交给了原处理器，原处理器已跑完。它里面的答复同样可能发进已释放的会话（会话已释放时发送什么也不做）。
        /// </summary>
        Handled,
        /// <summary>
        /// 没交给原处理器：框架释放了报文，是请求就调了发送答复。只说明框架调了发送，不说明对方收到。
        /// </summary>
        Answered,
        /// <summary>
        /// 等待之后重核没过：报文已释放，原处理器不跑；会话换掉的不答，目标换掉或关掉的请求答 ErrEntityNotFound。
        /// </summary>
        Dropped,
        /// <summary>
        /// 拦截器到达时抛出或等待的任务失败（报文释放，请求答 ErrRpcFail），或原处理器抛出（异常照原样往上抛）。
        /// </summary>
        Faulted
    }
#endif
}