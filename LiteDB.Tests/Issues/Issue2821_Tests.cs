using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2821_Tests
    {
        private sealed class DiskFullStream : MemoryStream
        {
            public bool FailNextWrite;
            public int Failures;
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    Failures++;
                    throw new IOException("injected disk full");
                }
                base.Write(buffer, offset, count);
            }
        }

        [Fact]
        public void Failed_initialization_releases_the_file_without_finalization()
        {
            using var file = new TempFile();
            Action open = () => { using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, InitialSize = 5000 }); };
            open.Should().Throw<LiteException>();
            using (File.Open(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }

        [Fact]
        public void Closed_instance_explains_recovery_after_a_transient_disk_full_failure()
        {
            using var data = new MemoryStream();
            using var log = new DiskFullStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            Exception subsequent;
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
                log.FailNextWrite = true;
                Action write = () => rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "not committed" });
                write.Should().Throw<IOException>().WithMessage("injected disk full");
                log.FailNextWrite.Should().BeFalse("the simulated storage fault has cleared");
                log.Failures.Should().Be(1);

                subsequent = Record.Exception(() => rows.Count());
                log.Failures.Should().Be(1, "the subsequent error is engine state, not another disk failure");
            }

            // The documented recovery is dispose/reopen. Check it before the diagnostic
            // assertion so a misleading error cannot hide lost data or leaked state.
            using (var recovered = new LiteDatabase(new LiteEngine(settings)))
            {
                var rows = recovered.GetCollection("rows");
                rows.FindAll().Select(row => row["_id"].AsInt32).Should().Equal(1);
                rows.FindById(1)["value"].AsString.Should().Be("acknowledged");
                rows.Insert(new BsonDocument { ["_id"] = 3, ["value"] = "recovered" });
            }
            using (var reopened = new LiteDatabase(new LiteEngine(settings)))
            {
                var rows = reopened.GetCollection("rows");
                rows.FindAll().Select(row => row["_id"].AsInt32).Should().Equal(1, 3);
                rows.FindById(3)["value"].AsString.Should().Be("recovered");
            }

            Assert.NotNull(subsequent);
            var diagnostic = subsequent.Message.ToLowerInvariant();
            (diagnostic.Contains("closed") || diagnostic.Contains("reopen") || diagnostic.Contains("recreat"))
                .Should().BeTrue("the user must be told the engine needs reopening after I/O failure, rather than that the disk is still full");
            subsequent.ToString().Should().Contain("injected disk full", "the original I/O cause must remain available");
        }

        [Fact]
        public void Reopening_after_disk_full_preserves_commits_and_accepts_new_writes_without_deleting_WAL()
        {
            using var data = new MemoryStream();
            using var log = new DiskFullStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 0;
                var col = db.GetCollection("rows");
                col.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                log.FailNextWrite = true;
                Action write = () => col.Insert(new BsonDocument { ["_id"] = 2, ["value"] = "failed" });
                write.Should().Throw<IOException>().WithMessage("injected disk full");
                log.Failures.Should().Be(1);
            }
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                var col = db.GetCollection("rows");
                col.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
                col.FindById(1)["value"].AsString.Should().Be("committed");
                col.Insert(new BsonDocument { ["_id"] = 3, ["value"] = "after recovery" });
            }
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                var col = db.GetCollection("rows");
                col.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(1, 3);
                col.FindById(3)["value"].AsString.Should().Be("after recovery");
            }
        }
    }
}
