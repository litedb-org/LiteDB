using System;
using System.Linq;
using System.Threading;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        // How long a call waits for a failed engine's teardown (on the thread that owns the failure)
        // before it reopens the engine read-only.
        private const int REOPEN_WAIT_MILLISECONDS = 30000;

        private readonly object _reopenLock = new object();

        // Set by Dispose: an engine the caller closed is never reopened.
        private volatile bool _closing;

        // Threads whose explicit transaction a write failure ended (decision 6): their Commit throws.
        private int[] _lostTransactions = Array.Empty<int>();

        /// <summary>
        /// Entry of every public operation. A write or sync failure stopped the engine (decision 6 of
        /// docs/decisions/durability-policy.md): reopen it read-only from the files as they are, so reads
        /// keep working and every write throws with the recorded failure until the database is reopened.
        /// Any other failure (a damaged file, a failed read) keeps the engine closed, as before.
        /// </summary>
        private void EnsureOpen()
        {
            var state = _state;
            if (state.Stopped && state.WriteFailure != null && !_closing) this.ReopenAfterWriteFailure(state);
            _state.Validate();
        }

        private void ReopenAfterWriteFailure(EngineState stopped)
        {
            lock (_reopenLock)
            {
                if (!ReferenceEquals(stopped, _state) || _closing) return;
                // The thread that owns the failure closes the services once it released its locks.
                if (!stopped.WaitClosed(REOPEN_WAIT_MILLISECONDS)) return;

                var failure = stopped.WriteFailure;
                var settings = _settings.Clone();
                settings.ReadOnly = true;
                settings.LegacyIndexScan = true;
                settings.WriteFailure = failure;
                settings.ReadOnlyCause = failure.ToString();
                _settings = settings;
                _lostTransactions = stopped.LostTransactionThreads ?? Array.Empty<int>();
                try
                {
                    this.Open();
                }
                catch (Exception reopen)
                {
                    // The files cannot be read either: later calls retry, and throw the failure meanwhile.
                    _state = stopped;
                    throw new System.IO.IOException(failure.ToString() + " Reopening it read-only failed too: " + reopen.Message, reopen);
                }
            }
        }

        /// <summary>
        /// Before a failure's teardown disposes the transactions: remember the threads with an explicit
        /// transaction, whose Commit must throw instead of finding no transaction after the reopen.
        /// </summary>
        private void RememberLostTransactions(EngineState origin)
        {
            if (origin.WriteFailure == null || _monitor == null) return;
            try
            {
                origin.LostTransactionThreads = _monitor.Transactions.Where(x => x.ExplicitTransaction)
                    .Select(x => x.ThreadID).Distinct().ToArray();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>The current thread's explicit transaction ended with a write failure: consume the mark.</summary>
        private bool TakeLostTransaction()
        {
            var thread = Thread.CurrentThread.ManagedThreadId;
            lock (_reopenLock)
            {
                if (!_lostTransactions.Contains(thread)) return false;
                _lostTransactions = _lostTransactions.Where(x => x != thread).ToArray();
                return true;
            }
        }

        private bool HasLostTransaction()
        {
            var thread = Thread.CurrentThread.ManagedThreadId;
            return _lostTransactions.Contains(thread);
        }
    }
}
