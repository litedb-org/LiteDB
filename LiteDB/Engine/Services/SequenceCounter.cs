using System;
using System.Threading;

namespace LiteDB.Engine
{
    // Kept in the engine's collection-name map. Updating a counter does not need
    // to replace a dictionary entry; DirectProcessHost facades share this object.
    internal sealed class SequenceCounter
    {
        private long _value;

        internal SequenceCounter(long value) => _value = value;
        internal long Value => Interlocked.Read(ref _value);
        internal long Next() => Interlocked.Increment(ref _value);

        internal void SetMax(long value)
        {
            var current = Value;
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref _value, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
