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

        // Held by a reopen from its start until it published every service, and by Dispose while it
        // closes: neither sees the other's services half-built.
        private readonly object _reopenLock = new object();

        // Set by Dispose: an engine the caller closed is never reopened.
        private volatile bool _closing;

        // Set while a reopen publishes the new services (Open assigns the state first): calls wait.
        private volatile bool _reopening;

        // Threads whose explicit transaction a write failure ended (decision 6): their Commit throws.
        private int[] _lostTransactions = Array.Empty<int>();

        /// <summary>
        /// Entry of every public operation. A write or sync failure stopped the engine (decision 6 of
        /// docs/decisions/durability-policy.md): reopen it read-only from the files as they are, so reads
        /// keep working and every write throws with the recorded failure until the database is reopened.
        /// Any other failure (a damaged file, a failed read) keeps the engine closed, as before. A call
        /// that arrives while another one reopens the engine waits until it finished.
        /// </summary>
        private void EnsureOpen()
        {
            // Read the state first: a reopen sets _reopening before Open publishes a new state, and
            // clears it once every service is in place. Its own thread does not wait for itself.
            var state = _state;
            if (_reopening && !Monitor.IsEntered(_reopenLock))
            {
                lock (_reopenLock) { }
                state = _state;
            }
            // A failure recorded where the engine could not stop (a $database read) stops it now.
            if (state.StopDue && !_closing) state.Stop(state.WriteFailure.Cause);
            // An in-memory or temporary database stays closed, as before decision 6: its failed engine's
            // teardown released the streams it lived in, and a reopen would read an empty database.
            if (state.Stopped && state.WriteFailure != null && !_closing && !_settings.EngineOwnsVolatileStreams)
            {
                this.ReopenAfterWriteFailure(state);
                state = _state;
            }
            // The state this call checked: one a failure stopped meanwhile throws its stop error.
            state.Validate();
        }

        private void ReopenAfterWriteFailure(EngineState stopped)
        {
            lock (_reopenLock)
            {
                if (!ReferenceEquals(stopped, _state) || _closing) return;
                // The thread that owns the failure closes the services once it released its locks.
                if (!stopped.WaitClosed(REOPEN_WAIT_MILLISECONDS) || _closing) return;

                var failure = stopped.WriteFailure;
                var settings = _settings.Clone();
                settings.ReadOnly = true;
                settings.LegacyIndexScan = true;
                settings.WriteFailure = failure;
                settings.ReadOnlyCause = failure.ToString();
                _settings = settings;
                _lostTransactions = stopped.LostTransactionThreads ?? Array.Empty<int>();
                _reopening = true;
                try
                {
#if DEBUG || TESTING
                    _settings.ReopenStage?.Invoke("before-open");
#endif
                    this.Open();
                }
                catch (Exception reopen)
                {
                    // The files cannot be read either: later calls retry, and throw the failure meanwhile.
                    _state = stopped;
                    throw new System.IO.IOException(failure.ToString() + " Reopening it read-only failed too: " + reopen.Message, reopen);
                }
                finally
                {
                    _reopening = false;
                }
            }
        }

        /// <summary>
        /// Close for Dispose: after a reopen that already started published its services (they are
        /// closed too), and before a later one can start (it sees <see cref="_closing"/>).
        /// </summary>
        private void CloseForDispose()
        {
            _closing = true;
            lock (_reopenLock) this.Close();
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

        /// <summary>
        /// The current thread's explicit transaction ended with a write failure: consume the mark. The
        /// first of BeginTrans, Commit or Rollback on that thread does.
        /// </summary>
        private bool TakeLostTransaction()
        {
            var thread = Thread.CurrentThread.ManagedThreadId;
            while (true)
            {
                var lost = Volatile.Read(ref _lostTransactions);
                if (!lost.Contains(thread)) return false;
                var rest = lost.Where(x => x != thread).ToArray();
                if (ReferenceEquals(Interlocked.CompareExchange(ref _lostTransactions, rest, lost), lost)) return true;
            }
        }
    }
}
