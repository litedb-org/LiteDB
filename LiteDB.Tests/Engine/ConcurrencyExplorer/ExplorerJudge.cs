using System;
using System.IO;
using System.Linq;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Outcome classes a scenario may permit for one operation. The set is stated per operation
    /// from the configuration (what races it), never from a known defect.
    /// </summary>
    [Flags]
    internal enum Permit
    {
        /// <summary>Only success.</summary>
        Success = 0,
        /// <summary>ObjectDisposedException, LiteException ENGINE_DISPOSED, or a pending acquisition cancelled by the close: the connection was closed under or before the call.</summary>
        Disposed = 1,
        /// <summary>InvalidOperationException / NotSupportedException / LiteException INVALID_TRANSACTION_STATE: a documented refusal.</summary>
        Refusal = 2,
        /// <summary>LiteException LOCK_TIMEOUT: a bounded wait gave up (a timed-out loser).</summary>
        LockTimeout = 4,
        /// <summary>IOException, or the error a fatally stopped engine reports.</summary>
        Fatal = 8,
        /// <summary>Any other LiteException.</summary>
        OtherLite = 16,
        Any = Disposed | Refusal | LockTimeout | Fatal | OtherLite
    }

    /// <summary>
    /// Counts <c>refusal:*</c> reachability markers per thread (M1's classification rule: an
    /// operation is REFUSED when it threw and a refusal marker fired on its thread during the
    /// call). The observer is chained onto <see cref="Reachability.Observer"/>, never assigned
    /// over another observer (the fuzz runner installs its own per run).
    /// </summary>
    internal static class ExplorerRefusals
    {
        [ThreadStatic] private static int _onThread;
        private static readonly object Gate = new object();
        private static readonly Action<string> Handler = OnMarker;

        /// <summary>Refusal markers counted on the calling thread so far.</summary>
        internal static int OnThread => _onThread;

        /// <summary>Chains the counter onto the reachability observer unless it is already there.</summary>
        internal static void Install()
        {
            lock (Gate)
            {
                var current = Reachability.Observer;
                if (current != null && current.GetInvocationList().Contains(Handler)) return;
                Reachability.Observer = current + Handler;
            }
        }

        private static void OnMarker(string marker)
        {
            if (marker.StartsWith("refusal:", StringComparison.Ordinal)) _onThread++;
        }
    }

    internal static class ExplorerJudge
    {
        /// <summary>
        /// The outcome class of <paramref name="error"/>; null for a type no library contract
        /// produces. <paramref name="refused"/>: a refusal marker fired on the calling thread during
        /// the call, so a library error that is not a disposal or a timed-out wait is a documented refusal.
        /// </summary>
        internal static Permit? Classify(Exception error, bool refused = false)
        {
            switch (error)
            {
                case ObjectDisposedException _: return Permit.Disposed;
                // A pending acquisition cancelled because its connection is closing (handle API revisions).
                case OperationCanceledException _: return Permit.Disposed;
                case LiteException lite when lite.ErrorCode == LiteException.ENGINE_DISPOSED: return Permit.Disposed;
                case LiteException lite when lite.ErrorCode == LiteException.LOCK_TIMEOUT: return Permit.LockTimeout;
                case LiteException _ when refused: return Permit.Refusal;
                case LiteException lite when lite.ErrorCode == LiteException.INVALID_TRANSACTION_STATE: return Permit.Refusal;
                case LiteException lite when lite.InnerException is IOException: return Permit.Fatal;
                case LiteException _: return Permit.OtherLite;
                case IOException _: return Permit.Fatal;
                case InvalidOperationException _: return Permit.Refusal;
                case NotSupportedException _: return Permit.Refusal;
                case TimeoutException _: return Permit.LockTimeout;
                default: return null;
            }
        }

        internal static string Describe(Exception error) => error == null ? "ok"
            : error.GetType().Name + (error is LiteException lite ? "#" + lite.ErrorCode : "") + ": " + error.Message;

        /// <summary>Judges one finished operation: success, or a failure of a permitted class.</summary>
        internal static void Judge(ExplorerSchedule schedule, ExplorerSchedule.Work work, Permit permitted)
        {
            work.Judged = true;
            schedule.Event("outcome " + work + " " + Describe(work.Failure) + (work.Refused ? " (refusal marker)" : ""));
            Judge(work.ToString(), work.Name, work.Failure, permitted, work.Refused);
        }

        internal static void Judge(string where, string op, Exception error, Permit permitted, bool refused = false)
        {
            if (error == null) return;
            if (error is ExplorerFailure failure) throw failure;
            // An oracle failure raised by a host (it carries a stable failure id) is never an operation outcome.
            if (error.GetType().GetProperty("FailureId") != null) throw new ExplorerFailure(ExplorerRun.DefaultFailureId(error), error.Message, error);
            var kind = Classify(error, refused);
            if (kind == null)
                throw new ExplorerFailure("UNEXPECTED_EXCEPTION_" + ExplorerFailure.Safe(error.GetType().Name),
                    where + " threw a type no library contract produces: " + error, error);
            if ((permitted & kind.Value) == 0)
                throw new ExplorerFailure("EXPLORER_UNPERMITTED_" + ExplorerFailure.Safe(op) + "_" + ExplorerFailure.Safe(kind.Value.ToString()),
                    where + " failed with " + Describe(error) + " where only " + permitted + " is permitted", error);
        }
    }
}
