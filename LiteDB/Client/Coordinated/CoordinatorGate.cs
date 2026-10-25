#if NET8_0_OR_GREATER
using System;
using System.Threading;

namespace LiteDB.Client.Coordinated
{
    /// <summary>
    /// Orders the coordinator's engine calls with a client opening a direct snapshot.
    /// Every engine call enters the gate; a snapshot grant waits until no call is
    /// running and then closes the gate until the client has opened its read-only
    /// engine, so that open never observes a commit, safepoint or checkpoint in
    /// progress. Waiting for quiescence does not block new calls (a call can wait
    /// for a transaction that only an idle session's next call would finish), so a
    /// grant under sustained load gives up and the client reads over IPC instead.
    /// </summary>
    internal sealed class CoordinatorGate
    {
        private readonly object _sync = new object();
        private readonly ThreadLocal<int> _depth = new ThreadLocal<int>();
        private int _active;
        private bool _closed;

        internal void Enter()
        {
            // Nested engine calls (for example from a document enumerator) re-enter.
            if (_depth.Value > 0)
            {
                _depth.Value++;
                return;
            }
            lock (_sync)
            {
                while (_closed) Monitor.Wait(_sync);
                _active++;
            }
            _depth.Value = 1;
        }

        internal void Exit()
        {
            if (--_depth.Value > 0) return;
            lock (_sync)
            {
                _active--;
                Monitor.PulseAll(_sync);
            }
        }

        internal T Run<T>(Func<T> action)
        {
            this.Enter();
            try { return action(); }
            finally { this.Exit(); }
        }

        /// <summary>Close the gate once no call runs, or return false after <paramref name="timeout"/>.</summary>
        internal bool TryClose(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_sync)
            {
                while (_active > 0 || _closed)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(_sync, remaining)) return false;
                }
                _closed = true;
                return true;
            }
        }

        internal void Open()
        {
            lock (_sync)
            {
                _closed = false;
                Monitor.PulseAll(_sync);
            }
        }
    }
}
#endif
