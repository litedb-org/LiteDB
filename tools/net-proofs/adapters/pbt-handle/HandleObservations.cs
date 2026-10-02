using System;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>Canonical observations of handle commands (documented refusals are values, not unexpected errors).</summary>
    public static class HandleObservations
    {
        public static readonly Observation Begun = Observation.Ok("begun");
        public static readonly Observation Paused = Observation.Ok("paused");
        public static readonly Observation Absent = Observation.Ok("absent");
        public static readonly Observation Overlap = Observation.Ok("refused:overlap");
        public static readonly Observation OverlapUnseen = Observation.Ok("refused:overlap-without-concurrent-call");
        public static readonly Observation ReadersOpen = Observation.Ok("refused:readers-open");
        public static readonly Observation ReadersOpenUnseen = Observation.Ok("refused:readers-open-without-concurrent-call");
        public static readonly Observation InvalidOperation = Observation.Ok("refused:invalid-operation");
        public static readonly Observation ObjectDisposed = Observation.Ok("refused:disposed");
        public static readonly Observation AdmissionTimeout = Observation.Ok("refused:admission-timeout");
        public static readonly Observation NotSupported = Observation.Ok("refused:not-supported");
        public static readonly Observation EarlyLockTimeout = Observation.Ok("LockTimeout:early");

        /// <summary>Result of a bulk insert whose input callback ran ordinary work first.</summary>
        public static Observation Callback(Observation callback, Observation insert) =>
            Observation.Ok("cb=" + callback + ";insert=" + insert);

        public static Observation Completion(string op) =>
            op == HandleAccessKind.Commit ? Observation.Ok("committed")
            : op == HandleAccessKind.Rollback ? Observation.Ok("rolled-back")
            : Observation.Ok("disposed");

        /// <summary>
        /// Map an exception of a handle call. <paramref name="overlapped"/>: another harness call on the
        /// same handle overlapped this one in real time, so an overlap refusal is legitimate.
        /// </summary>
        public static Observation FromException(Exception ex, bool overlapped)
        {
            switch (ex)
            {
                case LiteException _:
                    return Observation.FromException(ex);
                case ObjectDisposedException _:
                    return ObjectDisposed;
                case InvalidOperationException _ when ex.Message.IndexOf("Overlapping", StringComparison.Ordinal) >= 0 ||
                                                      ex.Message.IndexOf("reentrant", StringComparison.Ordinal) >= 0:
                    return overlapped ? Overlap : OverlapUnseen;
                case InvalidOperationException _ when ex.Message.IndexOf("readers", StringComparison.Ordinal) >= 0:
                    return overlapped ? ReadersOpen : ReadersOpenUnseen;
                case InvalidOperationException _:
                    return InvalidOperation;
                case TimeoutException _:
                    return AdmissionTimeout;
                case NotSupportedException _:
                    return NotSupported;
                default:
                    return Observation.UnexpectedException(ex);
            }
        }
    }
}
