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
