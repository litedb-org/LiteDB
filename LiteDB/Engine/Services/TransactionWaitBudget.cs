using System;
using System.Diagnostics;
using System.Threading;

namespace LiteDB.Engine
{
    // One sampled TIMEOUT per synchronous call. Only blocking admission/lock time
    // consumes it; user callbacks, I/O and commit durability are not timed out.
    internal sealed class TransactionWaitBudget : IDisposable
    {
        [ThreadStatic] private static TransactionWaitBudget _current;
        private readonly TransactionWaitBudget _previous;
        private readonly TransactionWaitBudget _budget;
        private TimeSpan _timeout;
        private TimeSpan _spent;

        internal TransactionWaitBudget(TimeSpan timeout)
        {
            _previous = _current;
            _budget = _previous?._budget ?? this;
            _timeout = timeout;
            _current = this;
        }

        internal static TimeSpan Timeout(TimeSpan fallback) => _current?._budget._timeout ?? fallback;

        internal static TimeSpan Remaining(TimeSpan fallback)
        {
            var budget = _current?._budget;
            if (budget == null) return fallback;
            var remaining = budget._timeout - budget._spent;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        internal static void Wait(object gate, TimeSpan remaining)
        {
            var budget = _current?._budget;
            var started = Stopwatch.GetTimestamp();
            try
            {
                TransactionBeginScope.Check();
                if (TransactionBeginScope.Token.CanBeCanceled && remaining > TimeSpan.FromMilliseconds(20))
                    remaining = TimeSpan.FromMilliseconds(20);
                Monitor.Wait(gate, remaining);
                TransactionBeginScope.Check();
            }
            finally
            {
                if (budget != null)
                    budget._spent += TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
            }
        }

        internal static void EnterSharedOwner(SemaphoreSlim gate, TimeSpan poll, Action checkExitedOwner)
        {
            while (true)
            {
                TransactionBeginScope.Check();
                var remaining = Remaining(TimeSpan.FromDays(20));
                var acquired = false;
                Block(() => acquired = gate.Wait(remaining < poll ? remaining : poll));
                if (acquired) return;
                if (remaining <= TimeSpan.Zero)
                    throw LiteException.LockTimeout("Shared local owner", Timeout(TimeSpan.FromSeconds(60)));
                checkExitedOwner();
            }
        }

        internal static void Clamp(TimeSpan timeout)
        {
            var budget = _current?._budget;
            if (budget != null && timeout < budget._timeout) budget._timeout = timeout;
        }

        internal static void Block(Action wait)
        {
            var budget = _current?._budget;
            var started = Stopwatch.GetTimestamp();
            try { TransactionBeginScope.Check(); wait(); }
            finally
            {
                if (budget != null)
                    budget._spent += TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency);
            }
        }

        public void Dispose() => _current = _previous;
    }

    internal sealed class TransactionWaitRefusedException : InvalidOperationException
    {
        internal object RequestOwner { get; }
        internal TransactionWaitRefusedException(object owner) : base("Cannot wait for a transaction executing on this callback thread.") { RequestOwner = owner; }
    }
}
