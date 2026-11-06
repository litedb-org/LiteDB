using System;
using System.Security.Cryptography;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Thread-owned SplitMix64 source for skip-list coin flips. The mixer is a
    /// permutation of 64-bit words; every output bit has an unbiased marginal.
    /// This is not a cryptographic random source.
    /// </summary>
    internal sealed class Randomizer
    {
        private const ulong Increment = 0x9E3779B97F4A7C15UL;
        private static long _seed = CreateSeed();
        private static readonly ThreadLocal<Randomizer> _current = new ThreadLocal<Randomizer>(
            () => new Randomizer(unchecked((ulong)Interlocked.Increment(ref _seed))));
        private ulong _state;

        public static Randomizer Current => _current.Value;

        private Randomizer(ulong seed)
        {
            _state = seed;
        }

        private static long CreateSeed()
        {
#if DEBUG || TESTING
            return RANDOMIZER_SEED;
#else
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return BitConverter.ToInt64(bytes, 0);
#endif
        }

        public uint Next()
        {
            unchecked
            {
                var value = (_state += Increment);
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return (uint)(value ^ (value >> 31));
            }
        }

#if DEBUG || TESTING
        // Explicit seeds are independent of thread scheduling and other tests.
        internal static Randomizer WithSeed(ulong seed) => new Randomizer(seed);
#endif
    }
}
