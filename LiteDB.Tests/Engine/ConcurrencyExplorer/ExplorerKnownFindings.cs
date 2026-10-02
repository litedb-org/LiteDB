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
                Id = "direct-fatal-stop-during-upload-releases-page-buffers-twice",
                Summary = "Direct: a fatal WAL write failure on another thread while FileStorage.Upload is paused in its source " +
                    "stream; the upload fails with 'Upload failed (page buffer ownership was transferred to disk) and its " +
                    "transaction could not be rolled back' and page buffers end with share count -1 (released twice), so the " +
                    "TESTING finalizer ENSURE 'share count must be 0 in destroy PageBuffer (current: -1)' terminates the process.",
                Fingerprint = @"^(EXPLORER_UNPERMITTED_UPLOAD_CALLBACK_\w+|EXPLORER_PAGE_BUFFER_FINALIZED_IN_USE)@callback-pause@mode=direct@access=\w+@maintenance=fatal@callback=[\w-]+$",
                Excludes = vector => vector.Configuration.Mode == ExplorerMode.Direct && vector.Configuration.Maintenance == ExplorerMaintenance.Fatal &&
                    vector.Scenario == "callback-pause" && vector.Variant % 4 == 2,
                Evidence = "class 1 vector callback-pause variant 42 seed 1, direct, ordinary, fatal: the xUnit replay crashed the test " +
                    "host in 1 of 4 runs (finalizer flush); in 200-step campaigns (seed 2947) the step-165 flush found eight buffers " +
                    "at -1 or the process crashed there (023c2b4ba twice, dev once), and a prefix replay failed at step 164 with the " +
                    "upload error above"
            },
            new ExplorerKnownFinding
            {
                Id = "shared-close-leaks-page-buffer",
                Summary = "Shared: after a run that disposed a connection while another actor's operation was paused in its " +
                    "callback, a page buffer became garbage with share count 1 (never released). In a TESTING process without " +
                    "the explorer's audit the finalizer ENSURE would terminate it.",
                Fingerprint = @"^EXPLORER_PAGE_BUFFER_FINALIZED_IN_USE@[\w-]+@mode=shared@access=\w+@maintenance=close@callback=[\w-]+$",
                Message = @"share count\(s\) 1 by the end",
                Evidence = "class 2: transaction-interleavings seed 16 step 39 on dev (callback-pause variant 17, shared, legacy, " +
                    "close, same-connection; the step before: transaction-contention variant 8, shared, legacy, close, dispose, " +
                    "external writer); neither vector reproduced it in 4 isolated replays each (schedule-dependent)"
            },
            new ExplorerKnownFinding
            {
                Id = "lock-timeout-pragma-stale-after-wal-restore",
                Summary = "LiteEngine.Open builds its LockService from the data file's header pragmas, then the WAL restore " +
                    "may replace the whole header. A connection that opens while the WAL holds a newer header (no checkpoint " +
                    "since TIMEOUT was set: CheckpointSize 0, a crash, an unclean close) reports TIMEOUT from the WAL header " +
                    "but waits on locks with the data file's value (default 1 min), and ignores later Timeout changes for its " +
                    "whole life. Observed as collection-cycle losers and a rebuild's exclusive wait taking 60 s, not 5 s.",
                Fingerprint = @"^DEADLINE_\w*WRITE_CROSS@collection-cycle@mode=direct@access=\w+@maintenance=none@callback=none$",
                Evidence = "class 1 (forced): every collection-cycle vector on dev (variants 0/1, plain and encrypted) with the " +
                    "fixture's TIMEOUT left in the WAL; reproduction outside the explorer: reopen after setting Timeout with " +
                    "CheckpointSize 0 -> LockService._pragmas.Timeout 00:01:00 while db.Timeout is 00:00:05 (M3 report). The " +
                    "fixture checkpoints its TIMEOUT to keep discovery going; LITEDB_EXPLORER_INCLUDE_KNOWN=1 skips that."
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
                Summary = "Shared: code running inside an explicit transaction that calls another connection to the same file on " +
                    "the same thread waits forever for the writer ownership its own thread holds: no refusal, no timeout. With " +
                    "BeginTrans this is the mechanism of open issue #3073 (an explicit transaction counts as an idle owner, so the " +
                    "#3072 refusal does not apply), reached here from a callback inside a call of that transaction. With " +
                    "FileStorage.Upload the transaction is the library's own (LiteStorage.Upload begins one and reads the caller's " +
                    "source stream under it), so a source-stream callback hangs where docs/shared-mode-safety.md promises a refusal " +
                    "for user code running inside a call: a defect beyond #3073.",
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
