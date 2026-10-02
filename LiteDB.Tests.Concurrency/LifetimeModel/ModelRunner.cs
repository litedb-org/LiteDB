using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Coyote;
using Microsoft.Coyote.Actors;
using Microsoft.Coyote.SystematicTesting;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    public enum ExplorationStrategy
    {
        Random,
        /// <summary>Coyote's priority-based (PCT-style) strategy with a few priority changes.</summary>
        Prioritization,
    }

    public sealed class ModelRunOptions
    {
        /// <summary>Default iterations per test; raise with LITEDB_COYOTE_ITERATIONS for campaigns.</summary>
        public const uint DefaultIterations = 1000;

        public uint Iterations { get; set; } = DefaultIterations;

        public uint Seed { get; set; } = 1;

        public uint MaxSteps { get; set; } = 3000;

        public ExplorationStrategy Strategy { get; set; } = ExplorationStrategy.Random;

        /// <summary>A Coyote reproducible trace (file content) to replay instead of exploring.</summary>
        public string ReplayTrace { get; set; }

        /// <summary>Applies LITEDB_COYOTE_ITERATIONS, LITEDB_COYOTE_SEED and LITEDB_COYOTE_REPLAY (a trace file path).</summary>
        public ModelRunOptions WithEnvironment()
        {
            if (uint.TryParse(Environment.GetEnvironmentVariable("LITEDB_COYOTE_ITERATIONS"), out var iterations)) this.Iterations = iterations;
            if (uint.TryParse(Environment.GetEnvironmentVariable("LITEDB_COYOTE_SEED"), out var seed)) this.Seed = seed;
            var replay = Environment.GetEnvironmentVariable("LITEDB_COYOTE_REPLAY");
            if (!string.IsNullOrEmpty(replay)) this.ReplayTrace = File.ReadAllText(replay);
            return this;
        }
    }

    public sealed class ModelRunResult
    {
        public string Name { get; set; }

        public bool BugFound { get; set; }

        public string Bug { get; set; }

        public uint IterationsRun { get; set; }

        /// <summary>1-based iteration in which the first bug was found, 0 when none.</summary>
        public uint FirstBugIteration { get; set; }

        public TimeSpan Elapsed { get; set; }

        public uint Seed { get; set; }

        public ExplorationStrategy Strategy { get; set; }

        /// <summary>Path of the reproducible trace written for a bug (replay with LITEDB_COYOTE_REPLAY).</summary>
        public string TraceFile { get; set; }

        /// <summary>Path of the bug report (message, model trace, Coyote readable trace).</summary>
        public string ReportFile { get; set; }

        public string Summary =>
            $"{this.Name}: {(this.BugFound ? $"BUG in iteration {this.FirstBugIteration}" : "no bug")} after {this.IterationsRun} " +
            $"iterations, seed {this.Seed}, {this.Strategy}, {this.Elapsed.TotalSeconds:0.0}s" +
            (this.TraceFile == null ? "" : $"; trace {this.TraceFile}; report {this.ReportFile}");
    }

    /// <summary>Runs a lifetime model under Coyote's systematic testing engine.</summary>
    public static class ModelRunner
    {
        public static ModelRunResult Run(string name, Func<LifetimeModel> factory, ModelRunOptions options)
        {
            var configuration = Configuration.Create()
                .WithTestingIterations(options.Iterations)
                .WithRandomGeneratorSeed(options.Seed)
                .WithMaxSchedulingSteps(options.MaxSteps)
                .WithTelemetryEnabled(false)
                .WithConsoleLoggingEnabled(false);
            configuration = options.Strategy == ExplorationStrategy.Prioritization
                ? configuration.WithPrioritizationStrategy(false, 5)
                : configuration.WithRandomStrategy();
            if (!string.IsNullOrEmpty(options.ReplayTrace)) configuration = configuration.WithReproducibleTrace(options.ReplayTrace);

            uint iterations = 0;
            var watch = Stopwatch.StartNew();
            using (var engine = TestingEngine.Create(configuration, (IActorRuntime runtime) =>
            {
                runtime.RegisterMonitor<LifetimeLivenessMonitor>();
                runtime.CreateActor(typeof(WorldActor), new WorldActor.Setup(factory));
            }))
            {
                // The engine stops at the first bug, so the last iteration started is the buggy one.
                engine.RegisterStartIterationCallBack(iteration => iterations = iteration + 1);
                engine.Run();
                watch.Stop();

                var report = engine.TestReport;
                var result = new ModelRunResult
                {
                    Name = name,
                    BugFound = report.NumOfFoundBugs > 0,
                    Bug = report.BugReports.FirstOrDefault(),
                    IterationsRun = iterations,
                    FirstBugIteration = report.NumOfFoundBugs > 0 ? iterations : 0,
                    Elapsed = watch.Elapsed,
                    Seed = options.Seed,
                    Strategy = options.Strategy,
                };
                if (result.BugFound) WriteArtifacts(result, engine);
                return result;
            }
        }

        private static void WriteArtifacts(ModelRunResult result, TestingEngine engine)
        {
            var directory = ArtifactDirectory();
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, $"{result.Name}-seed{result.Seed}-{result.Strategy}".ToLowerInvariant());
            result.TraceFile = stem + ".trace";
            result.ReportFile = stem + ".txt";
            File.WriteAllText(result.TraceFile, engine.ReproducibleTrace ?? string.Empty);
            File.WriteAllText(result.ReportFile, result.Summary + Environment.NewLine + result.Bug + Environment.NewLine +
                Environment.NewLine + "Coyote readable trace:" + Environment.NewLine + engine.ReadableTrace);
        }

        /// <summary>LITEDB_COYOTE_ARTIFACTS, else artifacts_temp/coyote at the repository root, else the temp directory.</summary>
        private static string ArtifactDirectory()
        {
            var configured = Environment.GetEnvironmentVariable("LITEDB_COYOTE_ARTIFACTS");
            if (!string.IsNullOrEmpty(configured)) return configured;
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LiteDB.sln"))) return Path.Combine(directory.FullName, "artifacts_temp", "coyote");
            }
            return Path.Combine(Path.GetTempPath(), "litedb-coyote");
        }
    }
}
