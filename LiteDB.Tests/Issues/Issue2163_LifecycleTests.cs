using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2163_LifecycleTests
    {
        [Fact]
        public void Rebuild_with_disabled_checkpoint_keeps_a_complete_standalone_backup()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed only in WAL" });
                new FileInfo(FileHelper.GetLogFile(file.Filename)).Length.Should().BeGreaterThan(0);
                db.Rebuild();
            }
            var backup = FileHelper.GetSuffixFile(file.Filename, "-backup", false);
            try
            {
                File.Exists(FileHelper.GetSuffixFile(FileHelper.GetLogFile(file.Filename), "-backup", false)).Should().BeFalse();
                using var reopened = new LiteDatabase(backup);
                reopened.GetCollection("rows").Count().Should().Be(1);
                reopened.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed only in WAL");
            }
            finally
            {
                File.Delete(backup);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void Failed_pre_rebuild_checkpoint_stops_engine_and_preserves_committed_wal(int successfulWrites)
        {
            using var file = new TempFile();
            var failure = new IOException("checkpoint data write failed");
            var writes = 0;
            using (var engine = new LiteEngine(file.Filename))
            using (var db = new LiteDatabase(engine))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                engine.SimulateDataWriteFail = _ =>
                {
                    if (writes++ == successfulWrites) throw failure;
                };
                Record.Exception(() => db.Rebuild()).Should().BeSameAs(failure);
                writes.Should().Be(successfulWrites + 1);
                engine.SimulateDataWriteFail = null;
                Record.Exception(() => db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 }))
                    .Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(failure,
                        "clearing the storage fault must not revive a partially checkpointed engine");
            }
            using (var reopened = new LiteDatabase(file.Filename))
            {
                reopened.GetCollection("rows").Count().Should().Be(1);
                Assert.NotNull(reopened.GetCollection("rows").FindById(1));
                reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rebuild_before_registration_rejects_the_old_operation_without_closing_new_engine(bool query)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(file.Filename);
            using var db = new LiteDatabase(engine);
            using var captured = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            engine.GetMonitor().BeforeTransactionRegistration = () =>
            {
                captured.Set();
                resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            var operation = Task.Run(() => Record.Exception(() =>
            {
                if (query) db.GetCollection("rows").Count();
                else db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            }));
            try
            {
                captured.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                db.Rebuild();
            }
            finally
            {
                resume.Set();
                operation.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }
            operation.Result.Should().BeOfType<ObjectDisposedException>();
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
            db.GetCollection("rows").Count().Should().Be(2);
        }
    }
}
