using System;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB
{
    public partial class SharedEngine
    {
        private SharedWaitRecorder _waitRecorder;

        // Never allocate on the acquisition path once created (a capturing lambda would, per call).
        private SharedWaitRecorder Waits => Volatile.Read(ref _waitRecorder) ?? this.CreateWaits();

        private SharedWaitRecorder CreateWaits()
        {
            var recorder = new SharedWaitRecorder(_mutexName, _settings.Filename, _settings.SharedSlowWaitThreshold, _settings.SharedSlowWait);
            return Interlocked.CompareExchange(ref _waitRecorder, recorder, null) ?? recorder;
        }

        /// <summary>
        /// A snapshot of this connection's waits for writer ownership: current waiters, the
        /// owner this process knows of, and statistics for the last <paramref name="window"/>
        /// (at least that long and at most one minute more, up to one hour; see
        /// <see cref="SharedWaitDiagnostics.Window"/>) and since creation.
        /// </summary>
        public SharedWaitDiagnostics GetWaitDiagnostics(TimeSpan window) => this.Waits.Snapshot(window);

        /// <summary>A snapshot covering the last five minutes.</summary>
        public SharedWaitDiagnostics GetWaitDiagnostics() => this.GetWaitDiagnostics(TimeSpan.FromMinutes(5));

        /// <summary>
        /// Opt-in (<c>SharedSelfWaitGrace</c>): whether this wait comes from the async flow that holds
        /// the active transaction handle owning writer ownership. A zero grace refuses it at once.
        /// </summary>
        private bool IsSelfWait()
        {
            var grace = _settings.SharedSelfWaitGrace;
            if (grace == Timeout.InfiniteTimeSpan || !SharedHandleRegistry.CurrentFlowHoldsOwner(_mutexName)) return false;
            if (grace != TimeSpan.Zero) return true;
            // Refused before any wait began: counted only as a refusal.
            this.Waits.Refused();
            throw SharedHandleRegistry.FlowSelfWait();
        }

        /// <summary>
        /// After a grace slice: refuse once the flow still holds the owner and it stayed idle that long.
        /// A refusal sets <paramref name="outcome"/>, so the caller ends its wait as refused, not counted.
        /// </summary>
        private bool StillSelfWaiting(ref SharedWaitRecorder.Outcome outcome)
        {
            if (SharedHandleRegistry.CurrentFlowHoldsOwner(_mutexName, _settings.SharedSelfWaitGrace))
            {
                outcome = SharedWaitRecorder.Outcome.Refused;
                throw SharedHandleRegistry.FlowSelfWait();
            }
            return SharedHandleRegistry.CurrentFlowHoldsOwner(_mutexName);
        }

        /// <summary>
        /// Run one blocking acquisition within <c>SharedWriterTimeout</c>, recording its wait. A self-wait
        /// waits in grace slices; a timed-out slice owns nothing, so it is simply attempted again.
        /// A handle's child records nothing: its begin records the local queue and this native wait
        /// as one wait, ending it when the native wait ended (<see cref="_admittedAt"/>).
        /// </summary>
        private T AcquireWithin<TState, T>(TState state, Func<SharedEngine, TState, SharedWaitDeadline, T> acquire)
        {
            var selfWait = this.IsSelfWait();
            var deadline = SharedWaitDeadline.Start(_settings.SharedWriterTimeout);
            var waits = this.Waits;
            var child = _transactionChild;
            var wait = child ? default : waits.Begin();
            var outcome = SharedWaitRecorder.Outcome.Acquired;
            try
            {
                while (true)
                {
                    try { return acquire(this, state, selfWait ? deadline.Within(_settings.SharedSelfWaitGrace) : deadline); }
                    catch (SharedWaitTimeoutException) when (selfWait && !deadline.Expired) { selfWait = this.StillSelfWaiting(ref outcome); }
                }
            }
            catch (SharedWaitTimeoutException error)
            {
                outcome = SharedWaitRecorder.Outcome.TimedOut;
                throw this.TimeoutError(waits, deadline, error.BehindThisConnection);
            }
            finally
            {
                if (child) Volatile.Write(ref _admittedAt, waits.Now());
                else waits.End(wait, outcome);
            }
        }

        // A handle's child: when its native wait ended, for the begin's single recorded wait.
        private long _admittedAt;

        /// <summary>
        /// The timeout error, attributed to the party the wait was behind. A handle's own child
        /// connection holds its local queue while it is admitted, so that queue says nothing there.
        /// </summary>
        private LiteException TimeoutError(SharedWaitRecorder waits, SharedWaitDeadline deadline, bool behindThisConnection) =>
            waits.TimeoutError(deadline.Timeout, behindThisConnection,
                handleAdmitting: !_transactionChild && TransactionWriters.TryGetValue(_mutexName, out var gate) && gate.CurrentCount == 0);
    }
}
