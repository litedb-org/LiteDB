using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Evidence for a failing case: generated inputs (seed, prefix, suffixes), the observed history of
    /// the real library (per-thread records with start/end clock order and results/exceptions), the
    /// execution boundaries, the environment, the model's verdict kept separately, the immediate
    /// replay rate, the shrunk counterexample and a classification of how the failure reproduces.
    /// </summary>
    public sealed class PropertyFailureReport
    {
        public int CaseSeed { get; internal set; }
        public string Environment { get; internal set; }
        public CaseFailure Original { get; internal set; }
        public PropertyCase OriginalCase { get; internal set; }
        public int ReplayRuns { get; internal set; }
        /// <summary>Immediate replays of the original inputs that failed with the same identity.</summary>
        public int ReplayFailures { get; internal set; }
        public PropertyCase ShrunkCase { get; internal set; }
        public CaseFailure ShrunkFailure { get; internal set; }
        public int ShrinkRuns { get; internal set; }
        public int ShrunkReproductions { get; internal set; }
        public string ArtifactPath { get; private set; }

        /// <summary>
        /// reproducible: every replay failed. schedule-dependent: some replays failed, or none did and
        /// operations of different threads overlapped (the verdict needs an interleaving that did not
        /// recur). environment-dependent: none did and a timing bound took part (lock timeout, hang or
        /// probe deadline). harness-nondeterminism: none did although nothing overlapped, so the inputs
        /// alone should have decided the outcome.
        /// </summary>
        public string Classification
        {
            get
            {
                if (this.ReplayFailures == this.ReplayRuns) return "reproducible";
                if (this.ReplayFailures > 0) return "schedule-dependent";
                if (this.Original.TimingBound) return "environment-dependent";
                return this.Original.Concurrent ? "schedule-dependent" : "harness-nondeterminism";
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"FAILURE [{this.Original.Identity}] case seed {this.CaseSeed}, mode {this.OriginalCase.Mode}");
            sb.AppendLine($"  replay inputs: LITEDB_PBT_SEED={this.CaseSeed} (or ParallelPropertyRunner.Run({this.CaseSeed}, 1, ...)) " +
                "regenerates the same commands; the thread schedule is not replayed.");
            sb.AppendLine($"  environment: {this.Environment}");
            sb.AppendLine($"  classification: {this.Classification}; immediate replays failed {this.ReplayFailures}/{this.ReplayRuns}" +
                (this.ReplayFailures == 0 ? " (kept as a finding)" : ""));
            if (this.ArtifactPath != null) sb.AppendLine("  evidence written to " + this.ArtifactPath);
            if (!ReferenceEquals(this.ShrunkCase, this.OriginalCase))
            {
                sb.AppendLine($"Shrunk counterexample ({this.ShrinkRuns} shrink runs; reproduced {this.ShrunkReproductions}/{this.ReplayRuns} re-runs):");
                sb.Append(this.ShrunkCase);
                sb.AppendLine(this.ShrunkFailure.ToString());
            }
            sb.AppendLine("Original case:");
            sb.Append(this.OriginalCase);
            sb.AppendLine(this.Original.ToString());
            return sb.ToString();
        }

        /// <summary>Write the report and the observed histories (JSON lines) when a directory is configured.</summary>
        internal void WriteArtifacts(string directory, TextWriter log)
        {
            if (string.IsNullOrEmpty(directory)) return;
            try
            {
                Directory.CreateDirectory(directory);
                var stem = Path.Combine(directory, $"pbt-{(this.OriginalCase.IsParallel ? "parallel" : "sequential")}-{this.OriginalCase.Mode}-seed{this.CaseSeed}");
                this.ArtifactPath = stem + ".txt";
                File.WriteAllText(this.ArtifactPath, this.ToString());
                WriteHistory(stem + "-history.jsonl", this.Original.Records);
                if (!ReferenceEquals(this.ShrunkFailure, this.Original)) WriteHistory(stem + "-shrunk-history.jsonl", this.ShrunkFailure?.Records);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.WriteLine("could not write evidence: " + ex.Message);
            }
        }

        private static void WriteHistory(string path, IReadOnlyList<OperationRecord> records)
        {
            if (records == null) return;
            File.WriteAllLines(path, records.OrderBy(r => r.Start).Select(r =>
                $"{{\"thread\":{r.Thread},\"index\":{r.Index},\"start\":{r.Start},\"end\":{(r.End == long.MaxValue ? "null" : r.End.ToString())}," +
                $"\"kind\":\"{r.Command.Kind}\",\"op\":\"{r.Command.Op}\",\"collection\":{r.Command.Collection},\"key\":{r.Command.Key}," +
                $"\"payload\":{r.Command.Payload},\"slot\":{r.Command.Slot},\"outcome\":\"{r.Result.Kind}\",\"value\":\"{Escape(r.Result.Value)}\"}}"));
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
