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
        /// (whole minutes, at most one hour) and since creation.
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
            if (grace == TimeSpan.Zero) throw this.RefuseSelfWait();
            return true;
        }

        /// <summary>After a grace slice: refuse once the flow still holds the owner and it stayed idle that long.</summary>
        private bool StillSelfWaiting()
        {
            if (SharedHandleRegistry.CurrentFlowHoldsOwner(_mutexName, _settings.SharedSelfWaitGrace)) throw this.RefuseSelfWait();
            return SharedHandleRegistry.CurrentFlowHoldsOwner(_mutexName);
        }

        private InvalidOperationException RefuseSelfWait()
        {
            this.Waits.Refused();
            return SharedHandleRegistry.FlowSelfWait();
        }

        /// <summary>
        /// Run one blocking acquisition within <c>SharedWriterTimeout</c>, recording its wait. A self-wait
        /// waits in grace slices; a timed-out slice owns nothing, so it is simply attempted again.
        /// </summary>
        private T AcquireWithin<TState, T>(TState state, Func<SharedEngine, TState, SharedWaitDeadline, T> acquire)
        {
            var selfWait = this.IsSelfWait();
            var deadline = SharedWaitDeadline.Start(_settings.SharedWriterTimeout);
            var waits = this.Waits;
            var wait = waits.Begin();
            var timedOut = false;
            try
            {
                while (true)
                {
                    try { return acquire(this, state, selfWait ? deadline.Within(_settings.SharedSelfWaitGrace) : deadline); }
                    catch (SharedWaitTimeoutException) when (selfWait && !deadline.Expired) { selfWait = this.StillSelfWaiting(); }
                }
            }
            catch (SharedWaitTimeoutException)
            {
                timedOut = true;
                throw waits.TimeoutError(deadline.Timeout);
            }
            finally { waits.End(wait, timedOut); }
        }
    }
}
