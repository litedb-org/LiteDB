using System;

namespace LiteDB.Engine
{
    internal sealed partial class VectorIndexService
    {
#if DEBUG || TESTING
        internal static Func<Random> LevelRandomFactory;
#endif

        private static Random CreateLevelRandom()
        {
#if DEBUG || TESTING
            return LevelRandomFactory?.Invoke() ?? new Random();
#else
            return new Random();
#endif
        }
    }
}
