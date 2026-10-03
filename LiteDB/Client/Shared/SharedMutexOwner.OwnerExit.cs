using System;
using System.Threading;

namespace LiteDB.Client.Shared
{
    internal sealed partial class SharedMutexOwner
    {
        /// <summary>
        /// If the owner thread exited while owning the mutex, have the holder close
        /// the connection's state and release it. Returns true when it did.
        /// </summary>
        /// <param name="inline">
        /// False for a bounded wait: the cleanup then runs on the thread pool (scoped owner) or on
        /// the holder's own poll, and the caller keeps waiting within its budget.
        /// </param>
        private bool ReleaseIfOwnerExited(bool inline = true)
        {
            Thread owner;
            var direct = false;
            lock (_sync)
            {
                owner = _owner ?? _scope.Owner;
                if (owner == null || owner.IsAlive) return false;
                if (ReferenceEquals(_scope.Owner, owner))
                {
                    // A scoped owner cannot leave its call without unwinding; if its thread
                    // died anyway, the OS abandoned its mutex and the next wait reports it.
                    direct = true;
                    _owner = null;
                    _scope.Owner = null;
                    _recursion = 0;
                    _generation++;
                }
            }
            if (direct)
            {
                if (inline) this.CleanUpExitedScope();
                else ThreadPool.UnsafeQueueUserWorkItem(_ => this.CleanUpExitedScope(), null);
                return true;
            }
            // The holder's poll releases an exited owner by itself (ReleaseExitedOwner is idempotent).
            if (inline) this.Send(Command.ReleaseExitedOwner);
            return true;
        }

        /// <summary>
        /// Wait for this connection's own release in flight. A call's own release is not another
        /// owner and is not charged to a budget: the holder completes it without waiting for
        /// anything else. An exited owner's cleanup (recovery that can close engine resources)
        /// is waited for only within a bounded budget.
        /// </summary>
        internal void WaitForOwnRelease(SharedWaitDeadline deadline)
        {
            if (deadline.IsInfinite || !_cleaningExited) { this.WaitForRelease(); return; }
            while (!_released.Wait(deadline.Slice(Poll))) deadline.ThrowIfExpired(behindThisConnection: true);
        }

        private void CleanUpExitedScope()
        {
            try { _ownerExited(); }
            catch (Exception) { /* The next open recovers. */ }
            _gate.Release();
        }
    }
}
