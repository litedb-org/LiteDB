using System;
using System.Collections.Generic;
using LiteDB.Engine;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>Knobs of one property run. Defaults keep an xUnit case at a few seconds.</summary>
    public sealed class PropertyOptions
    {
        public PropertyOptions(ConnectionType mode)
        {
            this.Mode = mode;
        }

        public ConnectionType Mode { get; }

        /// <summary>Key space 1..Keys per collection.</summary>
        public int Keys { get; set; } = 5;

        /// <summary>Concurrent suffix threads per parallel case, drawn from [Min, Max].</summary>
        public int MinSuffixThreads { get; set; } = 2;
        public int MaxSuffixThreads { get; set; } = 3;

        /// <summary>Commands per suffix thread, drawn from [1, Max].</summary>
        public int MaxSuffixLength { get; set; } = 6;

        /// <summary>Commands of the sequential prefix, drawn from [0, Max].</summary>
        public int MaxPrefixLength { get; set; } = 6;

        /// <summary>Commands of a sequential-property case.</summary>
        public int SequentialLength { get; set; } = 24;

        /// <summary>
        /// The TIMEOUT pragma (whole seconds): how long a write waits for a collection lock held by
        /// another transaction before LOCK_TIMEOUT. Small, so a real conflict costs little.
        /// </summary>
        public int LockTimeoutSeconds { get; set; } = 1;

        /// <summary>
        /// "durable commits" of the connection. Off by default: a device sync under the collection
        /// lock adds latency (and, under machine load, spurious lock waits) without changing the
        /// visibility or locking semantics this property checks.
        /// </summary>
        public bool DurableCommits { get; set; } = false;

        /// <summary>Liveness bound for every suffix thread to finish its commands.</summary>
        public TimeSpan HangDeadline { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>Bound for the post-run probe that writes every collection and reads the final state.</summary>
        public TimeSpan ProbeDeadline { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>Runs of each shrink candidate of a parallel failure (scheduling is not replayable).</summary>
        public int ShrinkAttempts { get; set; } = 6;

        /// <summary>Total executions the shrinker may spend.</summary>
        public int ShrinkBudget { get; set; } = 300;

        /// <summary>Re-runs of the shrunk counterexample to report its reproduction rate.</summary>
        public int ReproductionRuns { get; set; } = 20;

        /// <summary>Generate the cases <see cref="KnownFindings"/> excludes (the property then fails with them).</summary>
        public bool IncludeKnownFindings { get; set; } = KnownFindings.IncludeByDefault;

        /// <summary>
        /// Access kinds to generate from (null: every kind registered in this build). Default from
        /// <c>LITEDB_PBT_ACCESS_KINDS</c> (comma separated). A kind missing from the build makes the
        /// run NOT APPLICABLE.
        /// </summary>
        public IReadOnlyList<string> AccessKindNames { get; set; } = PropertyEnvironment.AccessKinds();

        /// <summary>Directory for failure evidence files (default <c>LITEDB_PBT_ARTIFACT_DIR</c>; null: none written).</summary>
        public string ArtifactDirectory { get; set; } = Environment.GetEnvironmentVariable("LITEDB_PBT_ARTIFACT_DIR");

        /// <summary>Optional decorator around the engine (self-tests inject deliberately broken engines).</summary>
        public Func<ILiteEngine, ILiteEngine> EngineDecorator { get; set; }

        public int ShrinkAttemptsFor(bool parallel) => parallel ? this.ShrinkAttempts : 1;
    }
}
