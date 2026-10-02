using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Environment knobs and description. <c>LITEDB_PBT_ITERATIONS</c> raises the case count for
    /// campaigns, <c>LITEDB_PBT_SEED</c> replays exactly one case seed, <c>LITEDB_PBT_BASE_SEED</c>
    /// moves the first seed of a campaign, <c>LITEDB_PBT_ACCESS_KINDS</c> selects access kinds,
    /// <c>LITEDB_PBT_ARTIFACT_DIR</c> receives failure evidence and
    /// <c>LITEDB_PBT_INCLUDE_KNOWN_FINDINGS=1</c> generates what <see cref="KnownFindings"/> excludes.
    /// </summary>
    public static class PropertyEnvironment
    {
        public static int Count(int defaultCount)
        {
            if (Seed() != null) return 1;
            var value = Environment.GetEnvironmentVariable("LITEDB_PBT_ITERATIONS");
            return int.TryParse(value, out var count) && count > 0 ? count : defaultCount;
        }

        public static int FirstSeed(int defaultSeed)
        {
            var replay = Seed();
            if (replay != null) return replay.Value;
            var value = Environment.GetEnvironmentVariable("LITEDB_PBT_BASE_SEED");
            return int.TryParse(value, out var seed) ? seed : defaultSeed;
        }

        public static IReadOnlyList<string> AccessKinds()
        {
            var value = Environment.GetEnvironmentVariable("LITEDB_PBT_ACCESS_KINDS");
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        }

        private static int? Seed()
        {
            var value = Environment.GetEnvironmentVariable("LITEDB_PBT_SEED");
            return int.TryParse(value, out var seed) ? seed : (int?)null;
        }

        /// <summary>Runtime, platform and every option that shapes generation or timing.</summary>
        public static string Describe(PropertyOptions options)
        {
            var kinds = options.AccessKindNames == null ? "all registered" : string.Join(",", options.AccessKindNames);
            return $"tfm={TargetFramework} runtime={Environment.Version} os={Environment.OSVersion} " +
                $"processors={Environment.ProcessorCount} 64bit={Environment.Is64BitProcess} mode={options.Mode} " +
                $"timeoutPragma={options.LockTimeoutSeconds}s durableCommits={options.DurableCommits} " +
                $"hangDeadline={options.HangDeadline.TotalSeconds:0}s probeDeadline={options.ProbeDeadline.TotalSeconds:0}s " +
                $"keys={options.Keys} suffixThreads={options.MinSuffixThreads}-{options.MaxSuffixThreads} " +
                $"suffixLength<={options.MaxSuffixLength} prefixLength<={options.MaxPrefixLength} sequentialLength={options.SequentialLength} " +
                $"kinds={kinds} knownFindings={(options.IncludeKnownFindings ? "included" : "excluded")} " +
                $"decorator={(options.EngineDecorator == null ? "none" : "custom")}";
        }

        private const string TargetFramework =
#if NET10_0_OR_GREATER
            "net10.0";
#elif NET8_0_OR_GREATER
            "net8.0";
#elif NET481
            "net481";
#elif NET462
            "net462";
#else
            "unknown";
#endif
    }
}
