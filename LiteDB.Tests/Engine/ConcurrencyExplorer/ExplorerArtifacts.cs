using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>The outcome of one explorer request.</summary>
    internal sealed class ExplorerResult
    {
        public ExplorerVerdict Verdict { get; set; }
        public string NotApplicableReason { get; set; }
        public ExplorerVector Vector { get; set; }
        public string Directory { get; set; }
        public string FailureId { get; set; }
        public Exception Failure { get; set; }
        public string HistoryPath { get; set; }
        public string ArtifactPath { get; set; }
        public string[] Decisions { get; set; } = new string[0];
        /// <summary>The generated program of a class-2 scenario (null for forced schedules).</summary>
        public string Program { get; set; }
        public bool LiveWorker { get; set; }

        /// <summary>1: forced schedule (replay the recorded decisions); 2: native-thread stress (retain history).</summary>
        public int EvidenceClass { get; set; } = 1;

        /// <summary>Failure id plus the scenario shape: the precise signature known findings are registered under.</summary>
        public string Fingerprint => this.FailureId == null ? null : this.FailureId + "@" + ExplorerArtifacts.Shape(this.Vector);

        public bool Passed => this.Verdict == ExplorerVerdict.Passed;

        public override string ToString() => this.Verdict == ExplorerVerdict.NotApplicable
            ? "NOT APPLICABLE " + this.Vector + ": " + this.NotApplicableReason
            : this.Verdict.ToString().ToUpperInvariant() + " " + this.Vector +
              (this.FailureId == null ? "" : " " + this.FailureId + " (artifact " + this.ArtifactPath + ")");
    }

    /// <summary>
    /// Failure artifacts. Each failed run writes <c>explorer-failure.json</c> next to its retained
    /// fixture and history: the evidence class, vector, failure id, fingerprint, message, recorded
    /// decisions, environment and the replay instruction. Class 1 replay = run the same vector and
    /// compare the decisions and the failure id; class 2 retains inputs and history and keeps the
    /// finding even when a replay does not reproduce it.
    /// </summary>
    internal static class ExplorerArtifacts
    {
        internal const string FailureFile = "explorer-failure.json";

        /// <summary>The configuration-dependent part of a fingerprint: scenario, mode, access, maintenance, callback.</summary>
        internal static string Shape(ExplorerVector vector)
        {
            if (vector == null) return "unknown";
            var c = vector.Configuration;
            return vector.Scenario + "@mode=" + ExplorerConfiguration.Name(c.Mode) + "@access=" + c.Access +
                "@maintenance=" + ExplorerConfiguration.Name(c.Maintenance) + "@callback=" + ExplorerConfiguration.Name(c.Callback);
        }

        internal static string WriteFailure(ExplorerResult result, IExplorerScenario scenario)
        {
            var path = Path.Combine(result.Directory, FailureFile);
            var document = new BsonDocument
            {
                ["schemaVersion"] = 1,
                ["evidenceClass"] = result.EvidenceClass,
                ["evidence"] = result.EvidenceClass == 1
                    ? "controlled schedule: replay the vector; the same decisions must reproduce the same failure id"
                    : "native-thread stress: inputs, history and environment retained; a non-reproducing replay is classified, the finding is kept",
                ["vector"] = result.Vector?.ToString(),
                ["scenario"] = result.Vector?.Scenario,
                ["configuration"] = result.Vector?.Configuration.Signature,
                ["failureId"] = result.FailureId,
                ["fingerprint"] = result.Fingerprint,
                ["message"] = result.Failure?.Message,
                ["exception"] = result.Failure?.ToString(),
                ["liveWorker"] = result.LiveWorker,
                ["history"] = result.HistoryPath == null ? null : Path.GetFileName(result.HistoryPath),
                ["decisions"] = new BsonArray(result.Decisions.Select(decision => new BsonValue(decision))),
                ["program"] = result.Program,
                ["scenarioDescription"] = scenario?.Description,
                ["environment"] = Environment(),
                ["replay"] = Replay(result.Vector)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(document, indent: true));
            return path;
        }

        /// <summary>
        /// The command that reruns <paramref name="vector"/>: a lifetime-chaos program regenerates from its seed
        /// and step (the vector's variant) through LifetimeChaosReplay; every other scenario replays the vector.
        /// </summary>
        internal static string Replay(ExplorerVector vector)
        {
            const string Test = " dotnet test LiteDB.Tests -c Release -f net8.0 -p:TestingEnabled=true --settings tests.runsettings --filter ";
            return vector?.Scenario == LifetimeChaosProgram.ScenarioName
                ? "LITEDB_LIFETIME_CHAOS=" + vector.Seed + ":" + vector.Variant + Test + "FullyQualifiedName~LifetimeChaosReplay"
                : "LITEDB_EXPLORER_VECTOR='" + vector + "'" + Test + "FullyQualifiedName~ConcurrencyExplorerReplay";
        }

        internal static BsonDocument Environment() => new BsonDocument
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["processors"] = System.Environment.ProcessorCount,
            ["machine"] = System.Environment.MachineName
        };
    }
}
