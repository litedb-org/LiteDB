using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>A registered defect the explorer reaches on this revision (not fixed by this milestone).</summary>
    internal sealed class ExplorerKnownFinding
    {
        public string Id { get; set; }
        public string Summary { get; set; }
        /// <summary>Regex over the fingerprint (<c>FAILURE_ID@scenario@mode=..@access=..@maintenance=..@callback=..</c>).</summary>
        public string Fingerprint { get; set; }
        /// <summary>Optional regex over the failure message (the fingerprint alone is not precise enough for some ids).</summary>
        public string Message { get; set; }
        /// <summary>Vectors this finding would hang or crash; they are excluded unless LITEDB_EXPLORER_INCLUDE_KNOWN=1.</summary>
        public Func<ExplorerVector, bool> Excludes { get; set; }
        public string Evidence { get; set; }
    }

    /// <summary>
    /// Known findings: discovery continues past them (xUnit reports instead of failing; the fuzz
    /// targets record them in known-findings.jsonl and go on). A finding that would hang an actor or
    /// crash the process is excluded by a precise vector predicate instead, and the exclusion is
    /// reported and counted, never silent. Keep each entry as narrow as its evidence.
    /// </summary>
    internal static class ExplorerKnownFindings
    {
        internal const string IncludeVariable = "LITEDB_EXPLORER_INCLUDE_KNOWN";

        public static readonly IReadOnlyList<ExplorerKnownFinding> All = new List<ExplorerKnownFinding>
        {
            new ExplorerKnownFinding
            {
                Id = "direct-dispose-under-active-operation",
                Summary = "Direct: Dispose (or the fatal stop of a failed WAL write) while another thread's operation is active " +
                    "returns at once; the operation then fails with an internal ENSURE (LiteException 999 'transaction must be " +
                    "active to commit (current state: Disposed)') and page buffers keep a share count, whose finalizer ENSURE " +
                    "crashes a TESTING process (M5 finding 4).",
                Fingerprint = @"^EXPLORER_UNPERMITTED_\w+_OTHERLITE@[\w-]+@mode=direct@access=\w+@(maintenance=(close|fatal)@callback=[\w-]+|maintenance=\w+@callback=dispose)$",
                Message = @"LiteException#999: transaction must be active",
                Excludes = vector => vector.Configuration.Mode == ExplorerMode.Direct &&
                    (vector.Configuration.Maintenance == ExplorerMaintenance.Close || vector.Configuration.Callback == ExplorerCallback.Dispose ||
                     vector.Configuration.Maintenance == ExplorerMaintenance.Fatal && vector.Scenario == "callback-pause" && vector.Variant % 4 == 3),
                Evidence = "class 1 (forced): callback-pause, mode=direct, maintenance=close; and maintenance=fatal with A paused in its " +
                    "input sequence's finally (variants 15, 39); test host crash 'share count must be 0 in destroy PageBuffer'"
            },
            new ExplorerKnownFinding
            {
                Id = "shared-dispose-from-input-teardown",
                Summary = "Shared, auto-commit: Dispose of the connection from the input sequence's finally block (the insert " +
                    "tearing its input down) on the operation's own thread returns; the insert then fails with an internal ENSURE " +
                    "(LiteException 999 'transaction must be active to commit (current state: Disposed)') instead of a refusal or a " +
                    "disposed error. Same symptom as direct-dispose-under-active-operation, whose leaked page buffers crash a TESTING " +
                    "process from the finalizer, so the vectors are excluded.",
                Fingerprint = @"^EXPLORER_UNPERMITTED_\w+_OTHERLITE@callback-pause@mode=shared@access=ordinary@maintenance=\w+@callback=dispose$",
                Message = @"LiteException#999: transaction must be active",
                Excludes = vector => vector.Configuration.Shared && vector.Configuration.Callback == ExplorerCallback.Dispose &&
                    vector.Scenario == "callback-pause" && vector.Variant % 4 == 3 &&
                    ExplorerAccessKinds.Find(vector.Configuration.Access)?.Transactional != true,
                Evidence = "class 1 (forced): callback-pause variants 7, 15, 31, 39, 47 (point input-teardown), mode=shared, " +
                    "access=ordinary, callback=dispose, every maintenance; legacy access at the same point passes"
            },
            new ExplorerKnownFinding
            {
                Id = "shared-peer-call-inside-own-explicit-transaction",
                Summary = "Shared: code running inside an explicit transaction (BeginTrans, or FileStorage.Upload's own transaction " +
                    "while it reads the source stream) that calls another connection to the same file on the same thread waits " +
                    "forever for the writer ownership its own thread holds: no refusal, no timeout.",
                Fingerprint = @"^DEADLINE_\w+@(callback-pause|transaction-contention)@mode=shared@access=\w+@maintenance=\w+@callback=peer$",
                Excludes = vector => vector.Configuration.Shared && vector.Configuration.Callback == ExplorerCallback.Peer &&
                    ((vector.Scenario == "callback-pause" || vector.Scenario == "transaction-contention") &&
                        ExplorerAccessKinds.Find(vector.Configuration.Access)?.Transactional == true ||
                     vector.Scenario == "callback-pause" && vector.Variant % 4 == 2),
                Evidence = "class 1 (forced): callback-pause variant 12/13 access=legacy and variant 14/38 (upload) access=ordinary, " +
                    "mode=shared, callback=peer: every replay misses its deadline; the wait-for graph reports nothing (the holder is idle)"
            }
        };

        public static ExplorerKnownFinding Match(string fingerprint, string message = null) => fingerprint == null ? null
            : All.FirstOrDefault(finding => finding.Fingerprint != null && Regex.IsMatch(fingerprint, finding.Fingerprint) &&
                (finding.Message == null || message != null && Regex.IsMatch(message, finding.Message)));

        public static ExplorerKnownFinding Match(ExplorerResult result) =>
            result == null || result.Verdict != ExplorerVerdict.Failed ? null : Match(result.Fingerprint, result.Failure?.Message);

        /// <summary>True when LITEDB_EXPLORER_INCLUDE_KNOWN=1 (run the excluded hang/crash vectors too).</summary>
        public static bool IncludeKnown => Environment.GetEnvironmentVariable(IncludeVariable) == "1";

        /// <summary>The finding that excludes <paramref name="vector"/>; generated programs (lifetime-chaos) withhold such choices themselves.</summary>
        public static ExplorerKnownFinding Excluding(ExplorerVector vector)
        {
            if (IncludeKnown || vector.Scenario == LifetimeChaosProgram.ScenarioName) return null;
            return All.FirstOrDefault(finding => finding.Excludes != null && finding.Excludes(vector));
        }
    }
}
