using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// Findings that only Windows file-sharing semantics expose: Windows refuses to delete, move or open for
    /// writing a file another handle keeps open without the matching share mode, where POSIX unlinks, renames
    /// and opens. Each matches only on Windows, so no other platform's classification changes, and its "no
    /// longer reproduces" check is withheld elsewhere (<see cref="TeardownKnownFinding.NotObservableHere"/>).
    /// </summary>
    internal static partial class TeardownKnownFindings
    {
        private const string PosixOnly = "Windows only: POSIX deletes, renames and opens a file another handle keeps open";

        private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static IEnumerable<TeardownKnownFinding> WindowsFindings()
        {
            // A live query whose sort spilled keeps its rented scratch stream across the close (the drivers' default prior).
            yield return OpenScratch("LiteEngine.Close", baseline: true, steps: null, model: null, "baseline", "quiescent.scratch");
            yield return OpenScratch("LiteEngine.CloseOnError", baseline: true, steps: null, model: null, "baseline", "quiescent.scratch");
            yield return OpenScratch("LiteEngine.Dispose", baseline: true, steps: null, model: null, "quiescent.scratch");
            // Its armed cases close the scratch streams' pool first and the cleanup's engine close deletes the file.
            yield return OpenScratch("SortDisk.Dispose", baseline: true, steps: new string[0], model: null, "baseline");
            // The skip leaves the query's scratch stream rented (quiescent.handles, excused), so the later close cannot delete it.
            yield return OpenScratch("SortService.Dispose", baseline: false, steps: new[] { "SortService.Dispose.return-reader" },
                model: FaultModel.Skip, "quiescent.scratch");
            yield return new TeardownKnownFinding
            {
                Id = "rebuild-hides-close-failure-behind-sharing-violation", Path = "LiteEngine.Rebuild", Issue = "#3113",
                Steps = new[] { "LiteEngine.Close.disk", "DiskService.Dispose.data-pool", "DiskService.Dispose.log-pool" },
                Model = FaultModel.Skip, Mode = TeardownMode.Direct, Kinds = new[] { "disposition" },
                When = _ => IsWindows(), NotObservableHere = IsWindows() ? null : PosixOnly,
                Description = "LiteEngine.Rebuild closes the old engine with Close() and drops the returned failure list " +
                    "(Rebuild.cs), then builds the replacement. When closing the old data or WAL streams failed and left a " +
                    "handle open, the rebuild opens the data file for writing (FileReaderV8) or moves the WAL (RebuildService " +
                    "Install), which Windows refuses: Rebuild throws a sharing violation that does not carry the close failure " +
                    "that caused it. On POSIX the same rebuild succeeds and the close failure is silently discarded.",
                Reproduction = "TeardownKnownFinding_Tests.Known_finding_rebuild_drops_a_failed_close_and_on_windows_fails_on_its_handle " +
                    "(asserts the Windows sharing violation on Windows, the silent discard elsewhere); Windows sweep cases " +
                    "LiteEngine.Rebuild/committed/direct skip LiteEngine.Close.disk#1, DiskService.Dispose.data-pool#1, .log-pool#1"
            };
        }

        private static TeardownKnownFinding OpenScratch(string path, bool baseline, string[] steps, FaultModel? model,
            params string[] kinds) => new TeardownKnownFinding
        {
            Id = "sortdisk-delete-blocked-by-open-scratch-stream", Path = path, Issue = "#3112", Mode = TeardownMode.Direct,
            Baseline = baseline, Steps = steps, Model = model, Kinds = kinds,
            When = result => IsWindows() && result.Prior != null && result.Prior.SpilledSort,
            NotObservableHere = IsWindows() ? null : PosixOnly,
            Description = "SortDisk.Dispose closes its stream pool and then deletes the -tmp sort scratch file. The pool closes " +
                "only the streams that were returned to it; a query whose sort spilled keeps its rented scratch stream " +
                "(opened with FileShare.ReadWrite, without FileShare.Delete) until the query is disposed. Closing the engine " +
                "while such a query is open therefore fails the delete on Windows: LiteEngine.Close returns the sharing " +
                "violation, LiteEngine.Dispose discards it, SortDisk.Dispose throws it, and the scratch file (the sort keys) " +
                "remains after the query ends, until a later engine on the same file spills and closes cleanly. POSIX unlinks " +
                "the open file. Distinct from #3097 (a failure while closing the pool skips the delete on every platform).",
            Reproduction = "TeardownKnownFinding_Tests.Known_finding_windows_close_cannot_delete_the_scratch_of_an_open_spilled_query " +
                "(asserts the refused delete on Windows, the POSIX unlink elsewhere); Windows sweep cases: the baselines of " +
                "LiteEngine.Close/open-work, LiteEngine.CloseOnError/fatal, LiteEngine.Dispose/open-work and " +
                "SortDisk.Dispose/after-spill, the armed cases of the first three, and SortService.Dispose/spilled-reader " +
                "skip SortService.Dispose.return-reader#1"
        };
    }
}
