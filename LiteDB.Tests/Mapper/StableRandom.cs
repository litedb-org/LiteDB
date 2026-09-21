using System;

namespace LiteDB.Tests.Mapper
{
    /// <summary>
    /// Versioned xorshift32 generator used by persisted fuzz seeds. Its sequence
    /// is deliberately independent of the runtime's System.Random algorithm.
    /// </summary>
    internal sealed class StableRandom : Random
    {
        private uint _state;

        internal StableRandom(int seed)
        {
            _state = unchecked((uint)seed);
            if (_state == 0) _state = 0x6d2b79f5;
        }

        internal uint NextUInt32()
        {
            var value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return value;
        }

        public override int Next() => (int)(NextUInt32() % int.MaxValue);

        public override int Next(int maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
            return maxValue == 0 ? 0 : (int)(NextUInt32() % (uint)maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
            var range = (ulong)((long)maxValue - minValue);
            return range == 0 ? minValue : (int)(minValue + (long)(NextUInt32() % range));
        }

        public override void NextBytes(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextUInt32();
        }

#if NET8_0_OR_GREATER
        public override void NextBytes(Span<byte> buffer)
        {
            for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextUInt32();
        }

        public override long NextInt64() => (long)(NextUInt64() & long.MaxValue);

        public override long NextInt64(long maxValue)
        {
            if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
            return maxValue == 0 ? 0 : (long)(NextUInt64() % (ulong)maxValue);
        }

        public override long NextInt64(long minValue, long maxValue)
        {
            if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
            var range = (ulong)(maxValue - minValue);
            return range == 0 ? minValue : minValue + (long)(NextUInt64() % range);
        }
#endif

        protected override double Sample() => NextUInt32() / ((double)uint.MaxValue + 1);

#if NET8_0_OR_GREATER
        private ulong NextUInt64() => ((ulong)NextUInt32() << 32) | NextUInt32();
#endif
    }
}
