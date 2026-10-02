using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Hand-written histories with a known verdict: the permitted-history check must accept valid
    /// concurrent outcomes (not trivially strict) and reject impossible ones (not trivially permissive).
    /// </summary>
    public class PermittedHistoryChecker_Tests
    {
        private static readonly Observation True = Observation.Ok(true);
        private static readonly Observation False = Observation.Ok(false);

        [Fact]
        public void A_lock_timeout_while_another_transaction_holds_the_lock_is_permitted()
        {
            var history = new[]
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 3, 4),
                Op(2, 0, Ordinary(DataOperations.Insert, key: 2, payload: 7), Observation.LockTimeout, 5, 19),
                Op(1, 2, Legacy(LegacyAccess.Commit), True, 20, 21),
            };

            Assert.True(Check(history, "[1:5]").Permitted);
            Assert.False(Check(history, "[1:5,2:7]").Permitted);
        }

        [Fact]
        public void Crossed_transactions_may_both_time_out_and_both_lose_their_writes()
        {
            var history = new[]
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(2, 0, Legacy(LegacyAccess.BeginTrans), True, 3, 4),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 11), Observation.Ok(1), 5, 6),
                Op(2, 1, Legacy(DataOperations.Insert, key: 2, payload: 12, collection: 1), Observation.Ok(2), 7, 8),
                Op(2, 2, Legacy(DataOperations.Insert, key: 2, payload: 22), Observation.LockTimeout, 9, 11),
                Op(1, 2, Legacy(DataOperations.Insert, key: 1, payload: 21, collection: 1), Observation.LockTimeout, 10, 12),
                Op(1, 3, Legacy(LegacyAccess.Commit), False, 13, 14),
                Op(2, 3, Legacy(LegacyAccess.Commit), False, 15, 16),
            };

            Assert.True(Check(history, "[]", "[]").Permitted);
            Assert.False(Check(history, "[1:11]", "[]").Permitted);
        }

        [Fact]
        public void A_lock_timeout_without_a_conflicting_holder_is_not_permitted()
        {
            var history = new[]
            {
                Op(1, 0, Ordinary(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 1, 2),
                Op(2, 0, Ordinary(DataOperations.Insert, key: 2, payload: 7), Observation.LockTimeout, 3, 10),
            };

            Assert.False(Check(history, "[1:5]").Permitted);
        }

        [Fact]
        public void A_timed_out_loser_loses_its_whole_transaction()
        {
            var loser = new List<OperationRecord>
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 3, 4),
                Op(2, 0, Legacy(LegacyAccess.BeginTrans), True, 5, 6),
                Op(2, 1, Legacy(DataOperations.Upsert, key: 4, payload: 8, collection: 1), True, 7, 8),
                Op(2, 2, Legacy(DataOperations.Insert, key: 3, payload: 9), Observation.LockTimeout, 9, 20),
                Op(1, 2, Legacy(LegacyAccess.Commit), True, 21, 22),
            };

            // The failed insert rolled back the whole transaction (its write to c1 too); Commit then returns false.
            Assert.True(Check(loser.Plus(Op(2, 3, Legacy(LegacyAccess.Commit), False, 23, 24)), "[1:5]", "[]").Permitted);
            Assert.False(Check(loser.Plus(Op(2, 3, Legacy(LegacyAccess.Commit), True, 23, 24)), "[1:5]", "[]").Permitted);
            Assert.False(Check(loser.Plus(Op(2, 3, Legacy(LegacyAccess.Commit), False, 23, 24)), "[1:5]", "[4:8]").Permitted);
        }

        [Fact]
        public void A_read_must_see_completed_writes_and_only_written_values()
        {
            var write = Op(1, 0, Ordinary(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 1, 3);

            Assert.True(Check(new[] { write, Op(2, 0, Ordinary(DataOperations.FindById, key: 1), Observation.Ok("null"), 2, 4) }, "[1:5]").Permitted);
            Assert.False(Check(new[] { write, Op(2, 0, Ordinary(DataOperations.FindById, key: 1), Observation.Ok("null"), 4, 5) }, "[1:5]").Permitted);
            Assert.False(Check(new[] { write, Op(2, 0, Ordinary(DataOperations.FindById, key: 1), Observation.Ok("7"), 2, 4) }, "[1:5]").Permitted);
        }

        [Fact]
        public void An_uncommitted_write_is_never_visible_to_another_thread()
        {
            var history = new[]
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 3, 4),
                Op(2, 0, Ordinary(DataOperations.Count), Observation.Ok(1), 5, 6),
                Op(1, 2, Legacy(LegacyAccess.Rollback), True, 7, 8),
            };

            Assert.False(Check(history, "[]").Permitted);
        }

        [Fact]
        public void Commit_without_an_own_transaction_is_refused_only_while_another_explicit_transaction_is_open()
        {
            var owner = new[]
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 3, 4),
                Op(1, 2, Legacy(LegacyAccess.Commit), True, 9, 10),
            };

            Assert.True(Check(owner.Plus(Op(2, 0, Legacy(LegacyAccess.Commit), Observation.Error(0), 5, 6)), "[1:5]").Permitted);
            Assert.True(Check(owner.Plus(Op(2, 0, Legacy(LegacyAccess.Rollback), False, 5, 6)), "[1:5]").Permitted);
            Assert.False(Check(owner.Plus(Op(2, 0, Legacy(LegacyAccess.Commit), Observation.Error(0), 11, 12)), "[1:5]").Permitted);
        }

        [Fact]
        public void An_interrupted_commit_has_its_whole_effect_or_none()
        {
            var history = new[]
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 3, 4),
                Op(1, 2, Legacy(DataOperations.Insert, key: 2, payload: 6), Observation.Ok(2), 5, 6),
                Op(1, 3, Legacy(LegacyAccess.Commit), Observation.Uncertain, 7, long.MaxValue),
            };

            Assert.True(Check(history, "[1:5,2:6]").Permitted);
            Assert.True(Check(history, "[]").Permitted);
            Assert.False(Check(history, "[1:5]").Permitted);
        }

        [Fact]
        public void Shared_mode_permits_no_lock_timeout_because_other_threads_wait_for_the_transaction()
        {
            var history = new[]
            {
                Op(1, 0, Legacy(LegacyAccess.BeginTrans), True, 1, 2),
                Op(1, 1, Legacy(DataOperations.Insert, key: 1, payload: 5), Observation.Ok(1), 3, 4),
                Op(2, 0, Ordinary(DataOperations.Insert, key: 2, payload: 7), Observation.LockTimeout, 5, 19),
                Op(1, 2, Legacy(LegacyAccess.Commit), True, 20, 21),
            };

            Assert.False(Check(history, ConnectionType.Shared, "[1:5]").Permitted);
        }

        private static CheckResult Check(IEnumerable<OperationRecord> history, params string[] final) =>
            Check(history, ConnectionType.Direct, final);

        private static CheckResult Check(IEnumerable<OperationRecord> history, ConnectionType mode, params string[] final) =>
            PermittedHistoryChecker.Check(new ModelState(mode, final.Length, 5, 3), new List<OperationRecord>(history), final);

        private static PropertyCommand Legacy(string op, int key = 0, int payload = 0, int collection = 0) =>
            new PropertyCommand(LegacyAccess.KindName, op, collection, key, payload);

        private static PropertyCommand Ordinary(string op, int key = 0, int payload = 0) => new PropertyCommand(OrdinaryAccess.KindName, op, 0, key, payload);

        private static OperationRecord Op(int thread, int index, PropertyCommand command, Observation result, long start, long end) =>
            new OperationRecord(thread, index, command, result, start, end);
    }

    internal static class HistoryExtensions
    {
        public static IEnumerable<OperationRecord> Plus(this IEnumerable<OperationRecord> history, OperationRecord record)
        {
            foreach (var item in history) yield return item;
            yield return record;
        }
    }
}
