using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal static class MemoryProfileDefaults
    {
        internal static long GetCacheSize(MemoryProfile profile, bool memoryBacked)
        {
            switch (profile)
            {
                case MemoryProfile.Balanced: return memoryBacked ? MEMORY_CACHE_SIZE : DEFAULT_CACHE_SIZE;
                case MemoryProfile.LowMemory: return (memoryBacked ? 4L : 8L) * 1024 * 1024;
                case MemoryProfile.Throughput: return (memoryBacked ? 16L : 128L) * 1024 * 1024;
                default: throw new ArgumentOutOfRangeException(nameof(profile));
            }
        }

        internal static int GetTransactionPageLimit(MemoryProfile profile)
        {
            switch (profile)
            {
                case MemoryProfile.Balanced: return MAX_TRANSACTION_SIZE;
                case MemoryProfile.LowMemory: return 256;
                case MemoryProfile.Throughput: return 4000;
                default: throw new ArgumentOutOfRangeException(nameof(profile));
            }
        }

        internal static MemoryProfile Parse(string value)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "balanced": return MemoryProfile.Balanced;
                case "lowmemory": return MemoryProfile.LowMemory;
                case "throughput": return MemoryProfile.Throughput;
                default: throw new LiteException(0, "Invalid `memory profile`; expected Balanced, LowMemory, or Throughput");
            }
        }
    }
}
