using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The process-local queue Shared transaction-handle begins wait in, and a barrier that proves
    /// a begin reached it (rather than merely blocking somewhere).
    /// </summary>
    internal static class SharedHandleQueue
    {
        internal static SharedEngine EngineOf(LiteDatabase db) =>
            (SharedEngine)typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(db);

        internal static SemaphoreSlim Of(SharedEngine engine)
        {
            var name = (string)typeof(SharedEngine).GetField("_mutexName", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(engine);
            var writers = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(SharedEngine)
                .GetField("TransactionWriters", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            return writers[name];
        }

        internal static SemaphoreSlim Of(LiteDatabase db) => Of(EngineOf(db));

        /// <summary>Threads blocked in the semaphore; <c>CurrentCount</c> alone cannot tell.</summary>
        internal static int Waiters(SemaphoreSlim queue)
        {
            var field = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? throw new NotSupportedException("SemaphoreSlim.m_waitCount not found") : (int)field.GetValue(queue);
        }

        /// <summary>Wait until a begin is queued: the queue is taken and a thread waits in it.</summary>
        internal static bool WaitForQueuedBegin(SemaphoreSlim queue, TimeSpan timeout) =>
            SpinWait.SpinUntil(() => queue.CurrentCount == 0 && Waiters(queue) > 0, timeout);
    }
}
