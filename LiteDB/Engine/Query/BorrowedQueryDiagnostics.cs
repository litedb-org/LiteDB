using System.Diagnostics;
using System.Threading;

namespace LiteDB.Engine
{
    internal static class BorrowedQueryDiagnostics
    {
        private static readonly AsyncLocal<Counters> _current = new AsyncLocal<Counters>();

        public static long DocumentsExamined => Read(x => x.DocumentsExamined);
        public static long DocumentsMaterialized => Read(x => x.DocumentsMaterialized);
        public static long BorrowedPredicateExecutions => Read(x => x.BorrowedPredicateExecutions);
        public static long BorrowedPredicateFallbacks => Read(x => x.BorrowedPredicateFallbacks);

        public static void Reset() => _current.Value = new Counters();

        [Conditional("TESTING")]
        public static void Examined()
        {
            if (_current.Value != null) Interlocked.Increment(ref _current.Value.DocumentsExamined);
        }

        [Conditional("TESTING")]
        public static void Materialized()
        {
            if (_current.Value != null) Interlocked.Increment(ref _current.Value.DocumentsMaterialized);
        }

        [Conditional("TESTING")]
        public static void Executed()
        {
            if (_current.Value != null) Interlocked.Increment(ref _current.Value.BorrowedPredicateExecutions);
        }

        [Conditional("TESTING")]
        public static void FellBack()
        {
            if (_current.Value != null) Interlocked.Increment(ref _current.Value.BorrowedPredicateFallbacks);
        }

        private static long Read(System.Func<Counters, long> selector)
        {
            return _current.Value == null ? 0 : selector(_current.Value);
        }

        private sealed class Counters
        {
            public long DocumentsExamined;
            public long DocumentsMaterialized;
            public long BorrowedPredicateExecutions;
            public long BorrowedPredicateFallbacks;
        }
    }
}
