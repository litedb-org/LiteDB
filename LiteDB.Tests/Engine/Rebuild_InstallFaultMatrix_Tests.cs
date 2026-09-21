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
    /// The rebuild install/rollback contract, checked against every reachable combination of
    /// injected faults rather than hand-picked sequences:
    ///
    /// 1. Always: the install error is the one thrown and every rollback error is attached to it;
    ///    a WAL is never live beside a data file it does not belong to; a complete copy of the
    ///    acknowledged data survives; the handle that ran the rebuild never serves a database
    ///    that silently lacks acknowledged data.
    /// 2. With at most one rollback fault the live path holds a complete database (the original
    ///    pair or the replacement), the reported state says which, and nothing else is left over.
    /// 3. With more rollback faults the state may be "incomplete": reported and fail-stop, not repaired.
    /// </summary>
    public class Rebuild_InstallFaultMatrix_Tests
    {
        public static IEnumerable<object[]> Scenarios()
        {
            foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
            {
                yield return new object[] { RebuildChange.SetPassword, true, connection };
                yield return new object[] { RebuildChange.SetPassword, false, connection };
                yield return new object[] { RebuildChange.RemovePassword, true, connection };
                yield return new object[] { RebuildChange.Collation, true, connection };
                yield return new object[] { RebuildChange.None, true, connection };
            }
        }

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Every_reachable_fault_combination_honours_the_rollback_contract(
            RebuildChange change, bool wal, ConnectionType connection)
        {
            var scenario = new RebuildFaultScenario(change, wal, connection);
            var violations = new List<string>();
            var visited = new HashSet<string>();
            var pending = new Stack<string[]>();
            var states = new HashSet<string>();
            pending.Push(new string[0]);

            while (pending.Count > 0)
            {
                var faults = pending.Pop();
                if (!visited.Add(string.Join(",", faults))) continue;

                using (var run = new RebuildFaultRun(scenario, faults))
                {
                    run.Execute();

                    // A fault that was never reached reproduces an already-visited run.
                    if (run.Thrown.Count != faults.Length) continue;

                    states.Add(run.LiveState ?? "success");
                    violations.AddRange(Check(run).Select(problem => run + " => " + problem));

                    foreach (var hook in run.Hit.Distinct().Where(h => !faults.Contains(h)))
                    {
                        pending.Push(faults.Concat(new[] { hook }).OrderBy(x => x, StringComparer.Ordinal).ToArray());
                    }
                }
            }

            violations.Should().BeEmpty();

            // Guard the explorer itself: it must have driven the rollback into every outcome.
            states.Should().Contain(new[] { "success", RebuildService.LiveStateOriginal, RebuildService.LiveStateReplacement });
        }

        private static IEnumerable<string> Check(RebuildFaultRun run)
        {
            if (run.Thrown.Count == 0) return CheckSuccess(run);

            var problems = new List<string>();
            var rollbackFaults = run.Thrown.Skip(1).ToArray();

            if (!(run.Failure is IOException) || run.Failure.Message != "injected " + run.Thrown[0])
                problems.Add("the install failure was masked by: " + run.Failure);

            foreach (var fault in rollbackFaults)
            {
                if (run.RollbackErrors.All(error => error.Message != "injected " + fault))
                    problems.Add("rollback error not reported: " + fault);
            }

            if (File.Exists(run.LiveLog) && run.ReadCopy(run.Live, run.LiveLog, false) != RebuildFaultRun.OldValue)
                problems.Add("a live WAL sits beside a data file it does not belong to");

            if (run.SameHandleRead == "ROW-MISSING")
                problems.Add("the rebuilding handle silently lost acknowledged data");

            switch (run.LiveState)
            {
                case RebuildService.LiveStateOriginal:
                    if (!run.FilesAfter.SequenceEqual(run.FilesBefore))
                        problems.Add("restoring the original left other files: before=" + string.Join(",", run.FilesBefore));
                    if (run.ReadCopy(run.Live, run.LiveLog, false) != RebuildFaultRun.OldValue)
                        problems.Add("the restored original pair is not readable with the original settings");
                    break;

                case RebuildService.LiveStateReplacement:
                    if (File.Exists(run.LiveLog)) problems.Add("an original WAL is live beside the replacement");
                    if (File.Exists(run.Temp)) problems.Add("the replacement is published but a temp copy remains");
                    if (run.ReadCopy(run.Live, null, true) != RebuildFaultRun.OldValue)
                        problems.Add("the published replacement is not readable with the requested settings");
                    if (run.ReadCopy(run.Backup, run.BackupLog, false) != RebuildFaultRun.OldValue)
                        problems.Add("the original backup pair is not recoverable");
                    break;

                case RebuildService.LiveStateIncomplete:
                    if (rollbackFaults.Length < 2)
                        problems.Add("a single rollback fault must still leave a complete live database");
                    if (run.ReadCopy(run.Backup, run.BackupLog, false) != RebuildFaultRun.OldValue &&
                        run.ReadCopy(run.Temp, null, true) != RebuildFaultRun.OldValue)
                        problems.Add("no complete copy of the acknowledged data survived");
                    if (!run.SameHandleRead.StartsWith("THROWS:"))
                        problems.Add("the handle kept working on an incomplete rollback");
                    if (!run.FilesAfterRead.SequenceEqual(run.FilesAfter))
                        problems.Add("using the handle after an incomplete rollback changed the files");
                    break;

                default:
                    problems.Add("the failure does not report the live state");
                    break;
            }

            if (run.LiveState != RebuildService.LiveStateIncomplete &&
                run.Scenario.Connection == ConnectionType.Shared &&
                run.SameHandleRead != RebuildFaultRun.OldValue)
            {
                problems.Add("the shared handle cannot reopen the database it left live");
            }

            if (problems.Count == 0 && run.LiveState != RebuildService.LiveStateIncomplete)
            {
                problems.AddRange(CheckRetry(run));
            }

            return problems;
        }

        private static IEnumerable<string> CheckSuccess(RebuildFaultRun run)
        {
            if (run.Failure != null) yield return "rebuild failed without an injected fault: " + run.Failure;
            if (run.SameHandleRead != RebuildFaultRun.OldValue) yield return "the handle cannot read after a successful rebuild";
            if (File.Exists(run.Temp)) yield return "a successful rebuild left its temp file";
            if (run.ReadCopy(run.Live, run.LiveLog, true) != RebuildFaultRun.OldValue)
                yield return "the rebuilt database is not readable with the requested settings";
        }

        /// <summary>
        /// Leftovers of a failed rebuild must not block the next attempt.
        /// </summary>
        private static IEnumerable<string> CheckRetry(RebuildFaultRun run)
        {
            var replacementIsLive = run.LiveState == RebuildService.LiveStateReplacement;
            var connection = replacementIsLive
                ? run.Scenario.ReplacementConnection(run.Live)
                : run.Scenario.OriginalConnection(run.Live);

            string problem = null;
            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    db.Rebuild(replacementIsLive ? new RebuildOptions() : run.Scenario.CreateOptions());
                    if (db.GetCollection("rows").FindById(1)?["value"].AsString != RebuildFaultRun.OldValue)
                        problem = "a retried rebuild lost the acknowledged data";
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
