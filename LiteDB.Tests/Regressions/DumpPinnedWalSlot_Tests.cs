using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: a transaction's unconfirmed WAL page is rewritten in place at its
    /// previous slot (e9bec08f8) after <c>MemoryCache.Invalidate(position, Log)</c>, which ENSUREs
    /// that the cached frame is idle. <c>$dump</c>/<c>$page_list</c> run in the thread's explicit
    /// transaction with a read snapshot that reads the transaction's own safepointed pages from
    /// those WAL slots and keeps them pinned until the snapshot is cleared - after the next
    /// safepoint/commit has already rewritten them. The ENSURE threw INVALID_DATAFILE_STATE,
    /// which stopped the engine and marked the data file invalid. 5.0.21 appended every WAL page,
    /// so a pinned old slot stayed valid and the commit succeeded. A pinned slot is now kept and
    /// the new version appended.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class DumpPinnedWalSlot_Tests
    {
        [Theory]
        [InlineData("$dump({0})")]
        [InlineData("$page_list({0})")]
        public void System_collection_inside_an_explicit_transaction_does_not_break_the_next_commit(string source)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 50 };
            byte[] openData, openLog, committedData, committedLog;

            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var col = db.GetCollection("docs");
                col.Insert(new BsonDocument { ["_id"] = -1, ["payload"] = "committed" });
                db.BeginTrans().Should().BeTrue();
                col.Insert(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string('x', 500) }));

                var transaction = engine.GetMonitor().GetThreadTransaction();
                var dirty = transaction.Pages.DirtyPages.Keys.Where(id => id != 0).Take(2).ToArray();
                dirty.Should().HaveCount(2, "a safepoint must have moved pages into unconfirmed WAL slots");

                foreach (var pageID in dirty)
                {
                    db.Execute("SELECT $ FROM " + string.Format(source, pageID)).ToEnumerable().ToList().Should().NotBeEmpty();
                }

                col.Update(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string('y', 500) }));
                openData = data.ToArray();
                openLog = log.ToArray();
                db.Invoking(x => x.Commit()).Should().NotThrow();
                committedData = data.ToArray();
                committedLog = log.ToArray();
            }

            // Process crash before and after the commit, and the clean close.
            Recover(openData, openLog).Should().Be((1, 0));
            Recover(committedData, committedLog).Should().Be((1001, 1000));
            Recover(data.ToArray(), log.ToArray()).Should().Be((1001, 1000));
        }

        /// <summary>
        /// File-backed: every dirty page pinned by $dump, then commit, then two supersessions under a
        /// live reader and a partial checkpoint that retires frames (v13), then a process-crash image
        /// and the real files are recovered and checkpointed.
        /// </summary>
        [Fact]
        public void Pinned_slots_of_a_file_database_survive_commit_retirement_and_recovery()
        {
            using var file = new TempFile();
            var y = new string('y', 500);
            byte[] crashData, crashLog;

            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, TransactionPageLimit = 50 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var col = db.GetCollection("docs");
                col.Insert(new BsonDocument { ["_id"] = -1, ["payload"] = "committed" });
                db.BeginTrans().Should().BeTrue();
                col.Insert(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = new string('x', 500) }));
                foreach (var pageID in engine.GetMonitor().GetThreadTransaction().Pages.DirtyPages.Keys.Where(id => id != 0).ToArray())
                {
                    db.Execute($"SELECT $ FROM $dump({pageID})").ToEnumerable().ToList().Should().NotBeEmpty();
                }
                col.Update(Enumerable.Range(0, 1000).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = y }));
                db.Commit().Should().BeTrue();

                using (var reader = engine.Query("docs", new Query()))
                {
                    RunThread(() =>
                    {
                        col.Update(Enumerable.Range(0, 100).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = "z" }));
                        col.Update(Enumerable.Range(0, 100).Select(i => new BsonDocument { ["_id"] = i, ["payload"] = y }));
                        engine.Checkpoint();
                    });
                }

                crashData = ReadShared(file.Filename);
                crashLog = ReadShared(FileHelper.GetLogFile(file.Filename));
                crashData[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.MVCC_FILE_VERSION, "the partial checkpoint retired frames");
            }

            Recover(crashData, crashLog).Should().Be((1001, 1000));
            for (var round = 0; round < 2; round++)
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("docs").Count().Should().Be(1001);
                db.GetCollection("docs").Count(Query.EQ("payload", y)).Should().Be(1000);
                db.Checkpoint();
            }
        }

        private static byte[] ReadShared(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static void RunThread(Action action)
        {
            Exception failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            });
            thread.Start();
            thread.Join();
            if (failure != null) throw new AggregateException(failure);
        }

        private static (int count, int updated) Recover(byte[] dataBytes, byte[] logBytes)
        {
            using var data = new MemoryStream();
            data.Write(dataBytes, 0, dataBytes.Length);
            using var log = new MemoryStream();
            log.Write(logBytes, 0, logBytes.Length);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var col = db.GetCollection("docs");
            ((object)col.FindById(-1)).Should().NotBeNull();
            return (col.Count(), col.Count(Query.EQ("payload", new string('y', 500))));
        }
    }
}
