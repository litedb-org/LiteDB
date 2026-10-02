using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Safety
{
    /// <summary>
    /// A defect of the current code that the teardown sweep reproduces and that is not fixed yet. A
    /// case whose unexpected violations all fall within <see cref="Kinds"/> and that matches the
    /// path, step and model is classified known instead of failing; a finding that no case of its
    /// path reproduces any more fails the sweep, so it is removed together with its fix.
    /// </summary>
    internal sealed class TeardownKnownFinding
    {
        public string Id { get; set; }
        public string Path { get; set; }
        /// <summary>Step names matched by prefix; null matches every step (including the baseline when <see cref="Baseline"/>).</summary>
        public string[] Steps { get; set; }
        public FaultModel? Model { get; set; }
        public TeardownMode? Mode { get; set; }
        public bool Baseline { get; set; }
        public string[] Kinds { get; set; }
        public string Description { get; set; }
        public string Reproduction { get; set; }
        /// <summary>The upstream issue that tracks the defect (litedb-org/LiteDB).</summary>
        public string Issue { get; set; }
        /// <summary>Evidence class (1: controlled/replayed; 2: native scheduling).</summary>
        public int EvidenceClass { get; set; } = 1;

        public bool Matches(TeardownRunResult result)
        {
            var spec = result.Spec;
            if (!string.Equals(spec.Driver.Path, this.Path, StringComparison.Ordinal)) return false;
            if (this.Mode.HasValue && spec.Driver.Mode != this.Mode.Value) return false;
            if (spec.Baseline) { if (!this.Baseline) return false; }
            else
            {
                if (this.Model.HasValue && spec.Model != this.Model.Value) return false;
                if (this.Steps != null && !this.Steps.Any(step => spec.Step.StartsWith(step, StringComparison.Ordinal))) return false;
            }
            return result.Unexpected.All(item => this.Kinds.Contains(TeardownRunResult.KindOf(item), StringComparer.Ordinal));
        }
    }

    /// <summary>The registered teardown findings on this revision (see docs/teardown-sweep.md, "Known findings").</summary>
    internal static partial class TeardownKnownFindings
    {
        /// <summary>Steps whose failure propagates out of SharedEngine.Dispose before its later cleanup ran.</summary>
        private static readonly string[] SharedDisposeAborts =
        {
            "SharedEngine.Dispose.", "SharedEngine.ClosePin.coordination", "SharedMutexPin.Hold.close",
            "SharedMutexOwner.Exit.scope-release"
        };

        public static readonly IReadOnlyList<TeardownKnownFinding> All = Build(new List<TeardownKnownFinding>
        {
            SharedDisposeAbort("SharedEngine.Dispose"),
            SharedDisposeAbort("SharedEngine.ClosePin"),
            SharedDisposeAbort("SharedEngine.CheckpointOnDispose"),
            SharedDisposeAbort("SharedMutexOwner.ReleaseAll"),
            SharedDisposeAbort("LiteDatabase.Dispose"),
            ScratchLeft("LiteEngine.Close"),
            ScratchLeft("LiteEngine.CloseOnError"),
            ScratchLeft("LiteEngine.Dispose"),
            new TeardownKnownFinding
            {
                Id = "litedatabase-dispose-skips-engine-after-checkpoint-restore-failure", Path = "LiteDatabase.Dispose", Issue = "#3098",
                Steps = new[] { "LiteDatabase.Dispose.checkpoint-override" }, Mode = TeardownMode.Direct,
                Kinds = new[] { "connection.engine" },
                Description = "LiteDatabase.Dispose(bool) restores the CHECKPOINT override of a stream database with " +
                    "_engine.Pragma(...) before _engine.Dispose(), without try/finally (since #2652, bf3987fbc). When that pragma " +
                    "write fails without stopping the engine (the disposing thread still has an open transaction, or a lock " +
                    "timeout while another thread's transaction holds the database), Dispose throws and the engine is never " +
                    "disposed: its streams, transaction monitor and lock service stay alive, and the finalizer (disposing: false) " +
                    "does not dispose it either.",
                Reproduction = "TeardownKnownFinding_Tests.Known_finding_stream_database_dispose_with_an_open_transaction_throws_and_leaves_its_engine_open " +
                    "(natural trigger); sweep case LiteDatabase.Dispose/stream/direct fail-inside LiteDatabase.Dispose.checkpoint-override#1"
            },
        });

        private static TeardownKnownFinding SharedDisposeAbort(string path) => new TeardownKnownFinding
        {
            Id = "shared-dispose-aborts-remaining-cleanup", Path = path, Steps = SharedDisposeAborts, Mode = TeardownMode.Shared,
            Issue = "#3096",
            Kinds = new[] { "quiescent.handles", "quiescent.readers" },
            Description = "SharedEngine.Dispose runs its cleanup unguarded: a failing step (retiring the cached coordinated read, " +
                "waiting for the pin holder, which rethrows the holder's close failure, the final checkpoint's scoped release, " +
                "the reader registry, coordination) propagates out of Dispose and skips every later step. _disposed is already set, " +
                "so a second Dispose returns at once: the reader registry's slot/lease handles and the coordination files " +
                "(-shared-live, -shared-state) stay open until the process exits (since #3003, 3b9e579f1). " +
                "docs/rules/storage-ownership.md requires a release path for exceptions and repeated disposal.",
            Reproduction = "TeardownKnownFinding_Tests.Known_finding_shared_dispose_failure_skips_the_remaining_cleanup_for_good; " +
                "sweep cases SharedEngine.Dispose/open-work/shared fail-inside SharedEngine.Dispose.retire-reads#1 and .readers#1"
        };

        private static TeardownKnownFinding ScratchLeft(string path) => new TeardownKnownFinding
        {
            Id = "sortdisk-dispose-skips-scratch-delete", Path = path, Steps = new[] { "SortDisk.Dispose.pool" }, Issue = "#3097",
            Kinds = new[] { "quiescent.scratch" },
            Description = "SortDisk.Dispose runs _pool.Dispose() then _factory.Delete() without try/finally: when closing the " +
                "scratch streams fails, the -tmp sort scratch file is not deleted. LiteEngine.Close collects the error and does " +
                "not retry, so the scratch file (sort keys of the last spilled query) remains after the engine closed " +
                "(since c9eb2d9f2, 2019).",
            Reproduction = "TeardownKnownFinding_Tests.Known_finding_sort_scratch_survives_the_close_when_closing_its_streams_fails; " +
                "sweep case LiteEngine.Close/open-work/direct fail-inside SortDisk.Dispose.pool#1"
        };

        private static IReadOnlyList<TeardownKnownFinding> Build(List<TeardownKnownFinding> findings)
        {
            OverlayFindings(findings);
            return findings;
        }

        /// <summary>Findings of another revision's teardown paths, added by an overlay file.</summary>
        static partial void OverlayFindings(List<TeardownKnownFinding> findings);

        public static TeardownKnownFinding Match(TeardownRunResult result) => All.FirstOrDefault(finding => finding.Matches(result));
    }
}
