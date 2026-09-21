using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The rebuild install/rollback contract (docs/rebuild-recovery.md), checked against every
    /// reachable combination of injected faults rather than hand-picked sequences:
    ///
    /// 1. Always: the install error is the one thrown and every rollback error is attached to it;
    ///    a WAL is never live beside a data file it does not belong to; a complete copy of the
    ///    acknowledged data survives; no handle serves a database that lacks acknowledged data.
    /// 2. With at most one failed rollback move the live path holds a complete database (the
    ///    original pair or the replacement), the reported state says which, the rebuilding shared
    ///    handle keeps working, nothing else is left over and a retried rebuild succeeds.
    /// 3. Beyond that the recovery marker stays: every open is refused and nothing is repaired,
    ///    because each further compensation step would itself be fallible.
    ///
    /// Every reachable fault sequence is executed against the installation itself, which only
    /// renames files, using a replacement built once. Sequences with up to one rollback fault also
    /// run through LiteDatabase.Rebuild, where handles and retained settings are involved. Slow
    /// verifications run once per distinct disk state a sequence can end in.
    /// </summary>
    public class Rebuild_InstallFaultMatrix_Tests
    {
        // Rollback steps that move a database file. The marker and the candidate cleanup are
        // bookkeeping: their failure may block access but never counts against the move budget.
        private static readonly string[] RollbackMoves =
        {
            "before-candidate-rollback", "before-source-rollback", "before-log-rollback",
            "before-source-retraction", "before-candidate-republish"
        };

        // Every hook on the successful installation path, each a possible first fault.
        private static readonly string[] InstallFaults =
        {
            "before-recovery-marker", "before-recovery-marker-flush",
            "before-log-backup", "after-log-backup", "before-source-backup",
            "after-source-backup", "before-temp-install", "after-temp-install",
            "before-recovery-marker-delete"
        };

        // Hooks with no code between them and the previous hook: failing there is the same program
        // state, so one run confirms it instead of exploring the identical rollback tree again.
        private static readonly string[] SameStateAsPreviousFault = { "before-source-backup", "before-temp-install" };

        // Rollback faults explored below the first fault.
        private const int Exhaustive = int.MaxValue;
        private const int OneRollbackFault = 1;

        // A retry only depends on what a failed attempt left on disk, so one per disk state is enough.
        private static readonly HashSet<string> Retried = new HashSet<string>();

        // The install and its rollback only rename files, so they are explored exhaustively against
        // a replacement built once. Whether a WAL exists is the only input that changes their path.
        private static readonly RebuildFaultScenario[] InstallScenarios =
        {
            new RebuildFaultScenario(RebuildChange.SetPassword, true, ConnectionType.Direct),
            new RebuildFaultScenario(RebuildChange.SetPassword, false, ConnectionType.Direct)
        };

        // Through LiteDatabase.Rebuild, where handles and retained settings come in, up to the
        // guarantee that one rollback fault is survivable. Guarded handles: Issue2979_Tests.
        private static readonly RebuildFaultScenario[] AllScenarios =
        {
            new RebuildFaultScenario(RebuildChange.SetPassword, true, ConnectionType.Shared),
            new RebuildFaultScenario(RebuildChange.SetPassword, true, ConnectionType.Direct),
            new RebuildFaultScenario(RebuildChange.Collation, true, ConnectionType.Shared)
        };

        public static IEnumerable<object[]> Scenarios() =>
            AllScenarios.Select(x => new object[] { x.Change, x.Wal, x.Connection });

        public static IEnumerable<object[]> FirstFaults() =>
            from installOnly in new[] { true, false }
            from scenario in installOnly ? InstallScenarios : AllScenarios
            from fault in InstallFaults
            let depth = SameStateAsPreviousFault.Contains(fault) ? 0 : installOnly ? Exhaustive : OneRollbackFault
            select new object[] { scenario.Change, scenario.Wal, scenario.Connection, fault, depth, installOnly };

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Successful_rebuild_passes_every_install_fault_point(RebuildChange change, bool wal, ConnectionType connection)
        {
            using (var run = new RebuildFaultRun(new RebuildFaultScenario(change, wal, connection), new string[0]))
            {
                run.Execute();

                Check(run).Should().BeEmpty();

                // The matrix starts from these. A new hook must be added to it, not silently skipped.
                run.Hit.Should().Equal(InstallFaults);
            }
        }

        [Theory]
        [MemberData(nameof(FirstFaults))]
        public void Every_reachable_fault_combination_honours_the_rollback_contract(
            RebuildChange change, bool wal, ConnectionType connection, string firstFault, int rollbackFaults, bool installOnly)
        {
            var scenario = new RebuildFaultScenario(change, wal, connection);
            var violations = new List<string>();
            var visited = new HashSet<string>();
            var pending = new Stack<string[]>();
            var outcomes = new HashSet<string>();
            pending.Push(new[] { firstFault });

            while (pending.Count > 0)
            {
                var faults = pending.Pop();
                if (!visited.Add(string.Join(",", faults))) continue;

                using (var run = new RebuildFaultRun(scenario, faults, installOnly))
                {
                    run.Execute();
                    run.Thrown.Distinct().Should().BeEquivalentTo(faults, "the explorer only schedules reachable faults");

                    outcomes.Add(run.Blocked ? "blocked" : run.LiveState);
                    violations.AddRange(Check(run).Select(problem => run + " => " + problem));

                    if (faults.Length > rollbackFaults) continue;

                    foreach (var hook in run.ReachedAfterLastFault.Distinct().Where(h => !faults.Contains(h)))
                    {
                        pending.Push(faults.Concat(new[] { hook }).OrderBy(x => x, StringComparer.Ordinal).ToArray());
                    }
                }
            }

            violations.Should().BeEmpty();

            // Guard the explorer itself: a failure after publication must reach every outcome.
            if (firstFault == "after-temp-install" && rollbackFaults == Exhaustive)
            {
                outcomes.Should().Contain(new[]
                {
                    "blocked", RebuildService.LiveStateOriginal, RebuildService.LiveStateReplacement
                });
            }
        }

        private static IEnumerable<string> Check(RebuildFaultRun run)
        {
            if (run.Thrown.Count == 0) return CheckSuccess(run);

            var problems = new List<string>();

            if (!(run.Failure is IOException) || run.Failure.Message != "injected " + run.Thrown[0])
                problems.Add("the install failure was masked by: " + run.Failure);

            foreach (var fault in run.Thrown.Skip(1))
            {
                if (run.RollbackErrors.All(error => error.Message != "injected " + fault))
                    problems.Add("rollback error not reported: " + fault);
            }

            if (run.HadLiveLog && run.ReadCopy(run.Live, run.LiveLog, false) != RebuildFaultRun.OldValue)
                problems.Add("a live WAL sits beside a data file it does not belong to");

            if (!run.InstallOnly && run.SameHandleRead == "ROWS-MISSING")
                problems.Add("the rebuilding handle silently lost acknowledged data");

            problems.AddRange(run.Blocked ? CheckBlocked(run) : CheckLive(run));

            return problems;
        }

        private static IEnumerable<string> CheckBlocked(RebuildFaultRun run)
        {
            var failedMoves = run.Thrown.Skip(1).Count(RollbackMoves.Contains);
            var markerFault = run.Thrown.Any(fault => fault.StartsWith("before-recovery-marker"));

            if (failedMoves < 2 && !markerFault)
                yield return "a single failed rollback move must still leave a complete, accessible database";

            if (run.ReadCopy(run.Backup, run.BackupLog, false) != RebuildFaultRun.OldValue &&
                run.ReadCopy(run.Live, run.LiveLog, false) != RebuildFaultRun.OldValue &&
                run.ReadCopy(run.Live, run.BackupLog, false) != RebuildFaultRun.OldValue &&
                run.ReadCopy(run.Live, null, true) != RebuildFaultRun.OldValue &&
                run.ReadCopy(run.Temp, null, true) != RebuildFaultRun.OldValue)
                yield return "no complete copy of the acknowledged data survived";

            if (!run.InstallOnly && (!run.SameHandleRead.StartsWith("THROWS:") || !run.SameHandleWrite.StartsWith("THROWS:")))
                yield return "the rebuilding handle kept working: read=" + run.SameHandleRead + " write=" + run.SameHandleWrite;

            foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
            foreach (var replacementSettings in new[] { false, true })
            {
                var opened = run.OpenLive(connection, replacementSettings);
                if (!opened.StartsWith("THROWS:LiteException#" + LiteException.REBUILD_INCOMPLETE))
                    yield return $"a fresh {connection} open was not refused: {opened}";
            }

            if (!run.ListFiles().SequenceEqual(run.FilesAfter) || !(run.FilesAfterRefusedUse ?? run.FilesAfter).SequenceEqual(run.FilesAfter))
                yield return "refused access created or changed files";
        }

        private static IEnumerable<string> CheckLive(RebuildFaultRun run)
        {
            var problems = new List<string>();
            var state = run.LiveState;

            // A marker that could not be created fails before any file has moved.
            if (state == null && run.Thrown[0] == "before-recovery-marker") state = RebuildService.LiveStateOriginal;

            switch (state)
            {
                case RebuildService.LiveStateOriginal:
                    var expected = run.Thrown.Contains("before-candidate-cleanup")
                        ? run.FilesBefore.Concat(new[] { Path.GetFileName(run.Temp) }).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                        : run.FilesBefore;
                    if (!run.FilesAfter.SequenceEqual(expected))
                        problems.Add("restoring the original left other files; expected " + string.Join(",", expected));
                    if (run.ReadCopy(run.Live, run.LiveLog, false) != RebuildFaultRun.OldValue)
                        problems.Add("the restored original pair is not readable with the original settings");
                    break;

                case RebuildService.LiveStateReplacement:
                    if (run.HadLiveLog) problems.Add("an original WAL is live beside the replacement");
                    if (File.Exists(run.Temp)) problems.Add("the replacement is published but a temp copy remains");
                    if (run.ReadCopy(run.Live, null, true) != RebuildFaultRun.OldValue)
                        problems.Add("the published replacement is not readable with the requested settings");
                    if (run.ReadCopy(run.Backup, run.BackupLog, false) != RebuildFaultRun.OldValue)
                        problems.Add("the original backup pair is not recoverable");
                    break;

                default:
                    problems.Add("access is open but the failure reports no complete live database");
                    break;
            }

            if (!run.InstallOnly && run.Scenario.Connection == ConnectionType.Shared && run.SameHandleRead != RebuildFaultRun.OldValue)
                problems.Add("the shared handle cannot reopen the database it left live");

            if (problems.Count == 0 && Retried.Add(run.Signature))
                problems.AddRange(CheckRetry(run, state));

            return problems;
        }

        private static IEnumerable<string> CheckSuccess(RebuildFaultRun run)
        {
            if (run.Failure != null) yield return "rebuild failed without an injected fault: " + run.Failure;
            if (run.SameHandleRead != RebuildFaultRun.OldValue) yield return "the handle cannot read after a successful rebuild";
            if (File.Exists(run.Temp)) yield return "a successful rebuild left its temp file";
            if (File.Exists(run.Marker)) yield return "a successful rebuild left its recovery marker";
            if (run.ReadCopy(run.Live, run.LiveLog, true) != RebuildFaultRun.OldValue)
                yield return "the rebuilt database is not readable with the requested settings";
        }

        /// <summary>
        /// Leftovers of a failed rebuild must not block the next attempt, and its result must accept writes.
        /// </summary>
        private static IEnumerable<string> CheckRetry(RebuildFaultRun run, string state)
        {
            var replacementIsLive = state == RebuildService.LiveStateReplacement;
            var connection = replacementIsLive
                ? run.Scenario.ReplacementConnection(run.Live)
                : run.Scenario.OriginalConnection(run.Live);

            string problem = null;
            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    db.Rebuild(replacementIsLive ? new RebuildOptions() : run.Scenario.CreateOptions());

                    var rows = db.GetCollection("rows");
                    if (rows.Count() != 2 || rows.FindById(2)?["value"].AsString != "acknowledged")
                        problem = "a retried rebuild lost the acknowledged data";

                    rows.Insert(new BsonDocument { ["_id"] = 3 });
                    if (rows.Count() != 3) problem = "the rebuilt database does not accept writes";
                }
            }
            catch (Exception ex)
            {
                problem = "a retried rebuild failed: " + ex.Message;
            }

            if (problem != null) yield return problem;
        }
    }
}
