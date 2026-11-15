using System.Collections.Generic;
using Fantasy.Entitas;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.

namespace Fantasy.Async
{
    /// <summary>
    /// 协程锁组件
    /// </summary>
    public class CoroutineLockComponent : Entity
    {
        private long _lockId;
        private CoroutineLockPool _coroutineLockPool;
        internal WaitCoroutineLockPool WaitCoroutineLockPool { get; private set; }
        internal CoroutineLockQueuePool CoroutineLockQueuePool { get; private set; }
        private readonly Dictionary<long, CoroutineLock> _coroutineLocks = new Dictionary<long, CoroutineLock>();
        internal CoroutineLockComponent Initialize()
        {
            _coroutineLockPool = new CoroutineLockPool();
            CoroutineLockQueuePool = new CoroutineLockQueuePool();
            WaitCoroutineLockPool = new WaitCoroutineLockPool(this);
            return this;
        }
        
        internal long LockId => ++_lockId;
        
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public override void Dispose()
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        {
            if (IsDisposed)
            {
                return;
            }

            _lockId = 0;
            base.Dispose();
        }

        /// <summary>
        /// 创建一个新的协程锁
        /// 使用这个方法创建的协程锁，需要手动释放管理CoroutineLock。
        /// 不会再CoroutineLockComponent理进行管理。
        /// </summary>
        /// <param name="lockDuty">锁职责</param>
        /// <returns></returns>
        public CoroutineLock Create(long lockDuty)
        {
            var coroutineLock = _coroutineLockPool.Rent();
            coroutineLock.Initialize(this, ref lockDuty);
            return coroutineLock;
        }

        /// <summary>
        /// 请求一个协程锁。
        /// 使用这个方法创建的协程锁，会自动释放CoroutineLockQueueType。
        /// </summary>
        /// <param name="lockDuty">锁职责</param>
        /// <param name="waitForId">锁队列Id</param>
        /// <param name="tag">当某些锁超时，需要一个标记来方便排查问题，正常的情况下这个默认为null就可以。</param>
        /// <param name="time">设置锁的超时时间，让超过设置的时间会触发超时，保证锁不会因为某一个锁一直不解锁导致卡住的问题。</param>
        /// <returns>
        /// 返回的WaitCoroutineLock通过Dispose来解除这个锁、建议用using来保住这个锁。
        /// 也可以返回的WaitCoroutineLock通过CoroutineLockComponent.UnLock来解除这个锁。
        /// </returns>
        public FTask<WaitCoroutineLock> Wait(long lockDuty, long waitForId, string tag = null, int time = 30000)
        {
            if (!_coroutineLocks.TryGetValue(lockDuty, out var coroutineLock))
            {
                coroutineLock = _coroutineLockPool.Rent();
                coroutineLock.Initialize(this, ref lockDuty);
                _coroutineLocks.Add(lockDuty, coroutineLock);
            }

            return coroutineLock.Wait(waitForId, tag, time);
        }

        /// <summary>
        /// 解除一个协程锁。
        /// </summary>
        /// <param name="coroutineLockType"></param>
        /// <param name="waitingKey"></param>
        public void Release(long coroutineLockType, long waitingKey)
        {
            if (IsDisposed)
            {
                return;
            }
            
            if (!_coroutineLocks.TryGetValue(coroutineLockType, out var coroutineLock))
            {
                return;
            }
                
            coroutineLock.Release(waitingKey);
        }

        /// <summary>
        /// 观察一个协程锁此刻的状态：是否被持有、排队等待的个数、队首等待者的标记与超时时间。
        /// 交回的是一份值拷贝，不交出锁表与等待者，也不会为没有创建过的锁职责创建协程锁。
        /// 只能在所属Scene的线程上调用，写这些状态的也是这个线程。
        /// </summary>
        /// <param name="lockDuty">锁职责</param>
        /// <param name="waitForId">锁队列Id</param>
        /// <returns>这一刻的状态拷贝；该锁职责没有协程锁时为未持有、无人等待</returns>
        public CoroutineLockState Observe(long lockDuty, long waitForId)
        {
            if (!_coroutineLocks.TryGetValue(lockDuty, out var coroutineLock))
            {
                return default;
            }

            return coroutineLock.Observe(waitForId);
        }
    }
}