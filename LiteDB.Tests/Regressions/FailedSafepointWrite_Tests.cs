using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Any operation can reach a safepoint that writes the transaction's dirty pages, a query too.
    /// When that write failed with an exception the engine does not treat as fatal (any non-I/O
    /// exception, e.g. UnauthorizedAccessException for EACCES/EPERM or an exception from a caller
    /// stream), the query only reported it: the explicit transaction stayed active although the
    /// failed pages had been discarded, and Commit then published the rest, a collection whose
    /// every read failed ("get only index below highest index"). Such a transaction now can only
    /// roll back: a later write or Commit rolls it back and throws, and a later read throws.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class FailedSafepointWrite_Tests
    {
        [Theory]
        [InlineData("commit")]
        [InlineData("write")]
        [InlineData("read")]
        [InlineData("rollback")]
        public void Transaction_whose_query_safepoint_failed_to_write_only_rolls_back(string then)
        {
            using var data = new MemoryStream();
            using var log = new FailingLog();
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, TransactionPageLimit = 6 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Prepare(db);
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("rows").Insert(Enumerable.Range(2, 3).Select(id => Row(id)));
                log.FailNextFrame = true;
                Action query = () => db.GetCollection("big").FindAll().ToList();
                query.Should().Throw<InvalidOperationException>("the query's safepoint wrote the transaction's dirty pages");
                log.Failed.Should().BeTrue();

                Finish(db, then);
                AssertRows(db);
                AddAnother(db);
            }

            using var recovered = new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = new MemoryStream(data.ToArray()), LogStream = new MemoryStream(log.ToArray())
            }));
            AssertRows(recovered, 10);
        }

#if DEBUG || TESTING
        /// <summary>A real file whose WAL write fails before writing anything (EACCES).</summary>
        [Fact]
        public void File_backed_transaction_whose_safepoint_write_was_refused_only_rolls_back()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, TransactionPageLimit = 6 }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                Prepare(db);
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("rows").Insert(Enumerable.Range(2, 3).Select(id => Row(id)));
                var refused = 0;
                engine.SimulateDiskWriteFail = page => { if (refused++ == 0) throw new UnauthorizedAccessException("injected EACCES on a WAL write"); };
                Action query = () => db.GetCollection("big").FindAll().ToList();
                query.Should().Throw<UnauthorizedAccessException>();
                engine.SimulateDiskWriteFail = null;
                refused.Should().BeGreaterThan(0);

                Finish(db, "commit");
                AssertRows(db);
            }

            using var reopened = new LiteDatabase(file.Filename);
            AssertRows(reopened);
            File.Delete(FileHelper.GetLogFile(file.Filename));
        }
#endif

        private static void Prepare(LiteDatabase db)
        {
            db.CheckpointSize = 0;
            db.GetCollection("big").Insert(Enumerable.Range(1, 100).Select(id => new BsonDocument { ["_id"] = id, ["payload"] = new string('b', 2000) }));
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("rows").Insert(Row(1));
            db.Checkpoint();
        }

        private static void Finish(LiteDatabase db, string then)
        {
            if (then == "rollback") db.Rollback().Should().BeTrue();
            else if (then == "read")
            {
                // A point read reaches no safepoint: it used the snapshots whose pages the failed
                // write discarded, and could return a document of another collection.
                Action read = () => db.GetCollection("rows").FindById(1);
                read.Should().Throw<LiteException>().WithMessage("*can only be rolled back*");
                Action commit = () => db.Commit();
                commit.Should().Throw<LiteException>().WithMessage("*can only be rolled back*");
            }
            else if (then == "write")
            {
                Action write = () => db.GetCollection("rows").Insert(Row(5));
                write.Should().Throw<LiteException>().WithMessage("*can only be rolled back*");
                db.Commit().Should().BeFalse("the failed write rolled the transaction back");
            }
            else
            {
                Action commit = () => db.Commit();
                commit.Should().Throw<LiteException>().WithMessage("*can only be rolled back*");
            }
        }

        private static void AddAnother(LiteDatabase db) => db.GetCollection("rows").Insert(Row(10));

        private static void AssertRows(LiteDatabase db, params int[] more)
        {
            var rows = db.GetCollection("rows");
            var expected = new[] { 1 }.Concat(more).ToArray();
            rows.FindAll().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected, "the transaction is rolled back as a whole");
            rows.Find(Query.GTE("value", 0)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
            for (var id = 2; id <= 5; id++) (rows.FindById(id) == null).Should().BeTrue();
            db.GetCollection("big").Count().Should().Be(100);
        }

        private static BsonDocument Row(int id) => new BsonDocument
        {
            ["_id"] = id, ["value"] = id, ["payload"] = new string('r', 500)
        };

        /// <summary>A caller log stream whose next frame write fails before writing anything.</summary>
        private sealed class FailingLog : MemoryStream
        {
            internal bool FailNextFrame, Failed;

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (FailNextFrame && count == WalChecksum.FrameSize)
                {
                    FailNextFrame = false;
                    Failed = true;
                    throw new InvalidOperationException("injected WAL write failure");
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
