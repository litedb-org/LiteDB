#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 4 of docs/decisions/durability-policy.md: while only the data file cannot sync
    /// (#2242), commits stay durable in the WAL, and no checkpoint can move the WAL into the data
    /// file, so it grows. Past the WAL limit ("wal limit", EngineSettings.WalLimit, 1 GiB by default)
    /// a write that starts throws an IOException before it changes anything, and reads keep working.
    /// Each refused write first retries the data sync, so writes resume once the data file syncs, and
    /// the next checkpoint drains the WAL (implementation note 8). The refusal is not a write failure:
    /// nothing failed, so the engine stays writable. The limit applies with and without durable
    /// commits (proposed default D).
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class WalLimit_Tests
    {
        private const long Limit = 1024 * 1024;

        /// <summary>
        /// A database whose header synced, then its data file stops syncing. Every write that starts
        /// with the WAL at or below a 1 MB limit is accepted, durable in the WAL where commits are;
        /// the first that starts past it throws the WAL-limit error, as do an update and a new index,
        /// and neither file changes. Reads find every accepted row, whole, through the index, and a
        /// power loss keeps each (opted out of durable commits: the data file as last synced with the
        /// WAL written back as it is). Once the data file syncs, the next write is accepted, a
        /// checkpoint drains the WAL, and every row survives the power loss.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)] // opted out of durable commits
        public void Writes_throw_past_the_wal_limit_while_the_data_file_cannot_sync_and_resume_once_it_syncs(bool durableCommits)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var connection = $"Filename={file.Filename};wal limit=1MB" + (durableCommits ? "" : ";durable commits=false");
            using (var setup = new LiteDatabase(connection)) setup.GetCollection("rows").EnsureIndex("value"); // its header synced
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                using var db = new LiteDatabase(connection);
                Info(db)["walLimit"].AsInt64.Should().Be(Limit);
                var rows = 0;
                while (Info(db)["logFileSize"].AsInt64 <= Limit)
                {
                    rows.Should().BeLessThan(1000, "the WAL grows with every commit");
                    db.GetCollection("rows").Insert(Row(++rows)); // started at or below the limit: accepted
                }
                rows.Should().BeGreaterThan(10);
                var info = Info(db);
                info["walKept"].AsBoolean.Should().BeTrue();
                info["durableLogFlush"].AsBoolean.Should().Be(durableCommits, "commits stay durable in the WAL");

                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                var dataSyncs = power.DataSyncs;
                Action insert = () => db.GetCollection("rows").Insert(Row(rows + 1));
                Action update = () => db.GetCollection("rows").Update(Row(1));
                Action index = () => db.GetCollection("rows").EnsureIndex("payload");
                foreach (var write in new[] { insert, update, index }) AssertRefused(write);
                (power.DataSyncs - dataSyncs).Should().BeGreaterOrEqualTo(3, "each refused write first retried the data sync");
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1, "a refused write changes nothing");
                SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2);

                AssertRows(db, rows); // reads keep working
                info = Info(db);
                info["readOnly"].AsBoolean.Should().BeFalse("the limit is not a write failure");
                info["writeFailure"].IsNull.Should().BeTrue();
                if (durableCommits) power.AfterPowerLoss(x => AssertRows(x, rows));
                FilePowerLossModel.Open((power.Capture().Data, SyncPowerLossModel.ReadShared(logName)), x => AssertRows(x, rows));

                power.DataFails = false;
                insert(); // the refused write's data sync now succeeds
                rows++;
                db.Checkpoint();
                new FileInfo(logName).Length.Should().Be(0, "once the data file syncs, a checkpoint drains the WAL");
                Info(db)["walKept"].AsBoolean.Should().BeFalse();
                power.AfterPowerLoss(x => AssertRows(x, rows)); // the checkpoint synced both files
                db.GetCollection("rows").Insert(Row(++rows));
                AssertRows(db, rows);
                if (durableCommits) power.AfterPowerLoss(x => AssertRows(x, rows));
                FilePowerLossModel.Open((power.Capture().Data, SyncPowerLossModel.ReadShared(logName)), x => AssertRows(x, rows));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The limit is checked when a write starts (implementation note 8): an explicit transaction
        /// whose writes started below it commits past it, durable in the WAL, and the write after it
        /// throws. Past the limit an explicit transaction still starts and reads, its write throws
        /// before it changes anything, and it rolls back. Closing writes nothing and does not throw
        /// (decision 5). A reopen and a shared connection, whose engines know nothing of the earlier
        /// refusals, refuse writes the same way while reads stay exact and neither file changes; once
        /// the data file syncs, the shared connection's next write is accepted, a checkpoint drains
        /// the WAL, and a power loss keeps every row.
        /// </summary>
        [Fact]
        public void Transaction_started_below_the_wal_limit_commits_past_it_and_the_limit_holds_for_every_engine()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            var connection = $"Filename={file.Filename};wal limit=1MB";
            using (var setup = new LiteDatabase(connection)) setup.GetCollection("rows").EnsureIndex("value"); // its header synced
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            const int count = 400; // about 1.6 MB of documents: the transaction's commit passes the limit
            try
            {
                using (var db = new LiteDatabase(connection))
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("rows").Insert(Enumerable.Range(1, count).Select(Row));
                    Info(db)["logFileSize"].AsInt64.Should().BeLessOrEqualTo(Limit, "the transaction's pages reach the WAL at its commit");
                    db.Commit().Should().BeTrue("a transaction already running may commit past the limit");
                    var info = Info(db);
                    info["logFileSize"].AsInt64.Should().BeGreaterThan(Limit);
                    info["durableLogFlush"].AsBoolean.Should().BeTrue();
                    power.AfterPowerLoss(x => AssertRows(x, count));
                    AssertRefused(() => db.GetCollection("rows").Insert(Row(count + 1)));

                    db.BeginTrans().Should().BeTrue("an explicit transaction starts past the limit");
                    AssertRows(db, count);
                    AssertRefused(() => db.GetCollection("rows").Delete(1));
                    db.Rollback().Should().BeTrue();
                    AssertRows(db, count);
                }
                var files = (SyncPowerLossModel.ReadShared(file.Filename), SyncPowerLossModel.ReadShared(logName));
                files.Item2.Length.Should().BeGreaterThan((int)Limit, "closing kept the WAL");

                using (var reopened = new LiteDatabase(connection))
                {
                    AssertRows(reopened, count);
                    AssertRefused(() => reopened.GetCollection("rows").Insert(Row(count + 1)));
                    AssertRows(reopened, count);
                }
                using (var shared = new LiteDatabase(connection + ";connection=shared"))
                {
                    AssertRows(shared, count);
                    AssertRefused(() => shared.GetCollection("rows").Insert(Row(count + 1)));
                    AssertRefused(() => shared.GetCollection("rows").EnsureIndex("payload"));
                    AssertRows(shared, count);
                    Info(shared)["writeFailure"].IsNull.Should().BeTrue("the limit is not a write failure");
                    SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(files.Item1, "no refused write changed anything");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(files.Item2);
                    power.AfterPowerLoss(x => AssertRows(x, count));

                    power.DataFails = false;
                    shared.GetCollection("rows").Insert(Row(count + 1)); // its engine's data sync now succeeds
                    shared.Checkpoint();
                    (File.Exists(logName) ? new FileInfo(logName).Length : 0).Should().Be(0, "once the data file syncs, a checkpoint drains the WAL");
                    AssertRows(shared, count + 1);
                }
                power.AfterPowerLoss(x => AssertRows(x, count + 1));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>A write that started past the WAL limit threw before it changed anything.</summary>
        private static void AssertRefused(Action write) =>
            write.Should().Throw<IOException>().WithMessage("Cannot modify this database now: its log file (* MB) passed the WAL limit (1 MB) " +
                "while the data file cannot sync to the device (#2242)*");

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id,
            ["value"] = id % 5,
            ["payload"] = new string((char)('a' + id % 26), 4000) + id
        };

        /// <summary>"rows" holds exactly <see cref="Row"/> 1..<paramref name="count"/>, byte for byte, and the value index finds each.</summary>
        private static int AssertRows(LiteDatabase db, int count)
        {
            var rows = db.GetCollection("rows");
            var all = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            all.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            for (var i = 0; i < count; i++) BsonSerializer.Serialize(all[i]).Should().Equal(BsonSerializer.Serialize(Row(i + 1)));
            for (var value = 0; value < 5; value++)
                rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(Enumerable.Range(1, count).Where(id => id % 5 == value));
            rows.Count().Should().Be(count);
            return count;
        }

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();
    }
}
#endif
