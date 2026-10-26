using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    public class ReadOnlyStorageNoLog_Tests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void Non_writable_data_without_a_log_refuses_writes_before_acknowledgement(
            bool fileBacked, bool directEngine, bool explicitTransaction)
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "keep" });
            }
            var original = File.ReadAllBytes(file.Filename);
            using Stream data = fileBacked
                ? new FileStream(file.Filename, FileMode.Open, FileAccess.Read)
                : new MemoryStream(original, writable: false);
            var settings = new EngineSettings { DataStream = data };
            IOException refusal = null;

            using (var db = directEngine ? new LiteDatabase(new LiteEngine(settings)) : new LiteDatabase(data))
            {
                var rows = db.GetCollection("rows");
                if (explicitTransaction) db.BeginTrans().Should().BeTrue();
                try
                {
                    rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "new" });
                    if (explicitTransaction) db.Commit().Should().BeTrue();
                    rows.Count().Should().Be(2);
                }
                catch (IOException error)
                {
                    refusal = error;
                    rows.Count().Should().Be(1);
                    rows.Invoking(x => x.Update(new BsonDocument { ["_id"] = 1, ["value"] = "changed" }))
                        .Should().Throw<IOException>();
                    rows.Invoking(x => x.Delete(1)).Should().Throw<IOException>();
                    rows.Invoking(x => x.EnsureIndex("other")).Should().Throw<IOException>();
                    if (explicitTransaction) db.Commit().Should().BeTrue("a read-only transaction can still complete");
                    db.Invoking(x => x.CheckpointSize = 1).Should().Throw<IOException>();
                    db.BeginTrans().Should().BeTrue();
                    rows.Count().Should().Be(1);
                    db.Rollback().Should().BeTrue();
                }
            }

            data.CanRead.Should().BeTrue("the caller owns the data stream");
            settings.ReadOnly.Should().BeFalse();
            data.Dispose();
            File.ReadAllBytes(file.Filename).Should().Equal(original);
            using var reopened = fileBacked
                ? new LiteDatabase(file.Filename)
                : new LiteDatabase(new MemoryStream(original, writable: false));
            var ids = reopened.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32).ToArray();
            if (refusal == null)
                ids.Should().Contain(2, "an acknowledged insert must survive disposal and reopen");
            ids.Should().Equal(1);
            reopened.GetCollection("rows").Find(Query.EQ("value", "keep")).Single()["_id"].AsInt32.Should().Be(1);
            refusal.Should().NotBeNull("non-writable data without a caller log cannot retain writes");
            refusal.Message.Should().Contain("read-only");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Non_writable_data_with_a_caller_file_log_retains_commits_on_reopen(bool directEngine)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using (var seed = new LiteDatabase(dataFile.Filename))
            {
                seed.GetCollection("rows").EnsureIndex("value");
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "keep" });
            }
            var original = File.ReadAllBytes(dataFile.Filename);
            using (var data = new FileStream(dataFile.Filename, FileMode.Open, FileAccess.Read))
            using (var log = new FileStream(logFile.Filename, FileMode.Create, FileAccess.ReadWrite))
            {
                using (var db = directEngine
                    ? new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
                    : new LiteDatabase(data, null, log))
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("rows").DeleteMany(Query.EQ("value", "missing")).Should().Be(0);
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "new" });
                    db.Commit().Should().BeTrue();
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "abort" });
                    // Dispose must roll back the open transaction and retain the earlier commit.
                }
                data.CanRead.Should().BeTrue();
                log.CanWrite.Should().BeTrue();
            }
            File.ReadAllBytes(dataFile.Filename).Should().Equal(original);
            var committedLog = File.ReadAllBytes(logFile.Filename);
            committedLog.Should().NotBeEmpty();

            for (var attempt = 0; attempt < 2; attempt++)
            {
                using (var data = new FileStream(dataFile.Filename, FileMode.Open, FileAccess.Read))
                using (var log = new FileStream(logFile.Filename, FileMode.Open, FileAccess.ReadWrite))
                using (var db = new LiteDatabase(data, null, log))
                {
                    db.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id).Should().Equal(1, 2);
                    db.GetCollection("rows").Find(Query.EQ("value", "new")).Single()["_id"].AsInt32.Should().Be(2);
                }
                File.ReadAllBytes(dataFile.Filename).Should().Equal(original);
                File.ReadAllBytes(logFile.Filename).Should().Equal(committedLog);
            }
        }
    }
}
