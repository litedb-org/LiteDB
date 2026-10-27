#if NET8_0_OR_GREATER
using System;
using System.Threading;

namespace LiteDB.Client.Shared
{
    // Serialized across the operation and holder threads. Budgets start at the first observed pressure,
    // never in the middle of an active transaction or retained streaming result.
    internal sealed class SharedWriterTenure : IDisposable
    {
        internal const int TransactionBudget = 8;
        internal const int MillisecondBudget = 1;
        private readonly SharedMemoryAuthority _authority;
        private SharedMemoryReaderTable _table;
        private readonly object _gate = new object();
        private readonly int _transactionBudget, _millisecondBudget;
        private long _pressureAt = -1;
        private int _transactions;
        internal SharedWriterTenure(SharedMemoryAuthority authority,
            int transactionBudget = TransactionBudget, int millisecondBudget = MillisecondBudget)
        {
            _authority = authority;
            _transactionBudget = transactionBudget;
            _millisecondBudget = millisecondBudget;
        }
        internal SharedWriterTenure(SharedMemoryReaderTable table) : this(table.Authority)
        {
            if (!table.TryRetain()) throw new ObjectDisposedException(nameof(SharedMemoryReaderTable));
            _table = table;
        }
        public void Dispose() => Interlocked.Exchange(ref _table, null)?.Release();
        internal bool ShouldEnd(bool boundary = false)
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (!_authority.Requests.HasPressure(now))
                {
                    _pressureAt = -1;
                    _transactions = 0;
                    return false;
                }
                if (_pressureAt < 0) _pressureAt = now;
                if (boundary) _transactions++;
                return _transactions >= _transactionBudget || now - _pressureAt >= _millisecondBudget;
            }
        }
        internal void Wait()
        {
            var observed = _authority.Map.Load(SharedMemoryLayout.WriterWake);
            // Recheck pressure after reading the word: a request between the last
            // predicate and this read must not be lost by sleeping on its new value.
            int timeout;
            lock (_gate)
            {
                ShouldEnd();
                timeout = _pressureAt < 0 ? 20 : MillisecondBudget;
            }
            _authority.Map.WaitWake(observed, timeout);
        }
        internal void Wake() => _authority.Map.Wake();
    }
}
#endif
