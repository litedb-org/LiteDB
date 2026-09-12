using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2818_Tests
    {
        private sealed class DurableFile : FileStream
        {
            public int DurableFlushes { get; private set; }
            public byte[] DurableBytes { get; private set; } = new byte[0];
            public DurableFile(string path) : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }
            public override void Flush(bool flushToDisk)
            {
                base.Flush(flushToDisk);
                if (!flushToDisk) return;
                DurableFlushes++;
                var position = Position;
                Position = 0;
                using var copy = new MemoryStream();
                CopyTo(copy);
                DurableBytes = copy.ToArray();
                Position = position;
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Commit_flushes_log_to_disk_and_durable_snapshot_recovers_exact_rows(string password)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new DurableFile(dataFile.Filename);
            using var log = new DurableFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var col = db.GetCollection("rows");
            col.Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "before" });
            db.Checkpoint();
            var before = log.DurableFlushes;
            db.BeginTrans().Should().BeTrue();
            col.Insert(new BsonDocument { ["_id"] = 2, ["payload"] = new string('x', 20000) });
            db.Commit().Should().BeTrue();
            log.DurableFlushes.Should().BeGreaterThan(before, "commit must request durable flush before reporting success");
            // Reopen only bytes captured at Flush(true). Disposal/checkpoint must not
            // rescue an otherwise undurable commit before this independent read.
            using var crashData = new MemoryStream();
            using var crashLog = new MemoryStream();
            crashData.Write(data.DurableBytes, 0, data.DurableBytes.Length);
            crashLog.Write(log.DurableBytes, 0, log.DurableBytes.Length);
            using var recoveredEngine = new LiteEngine(new EngineSettings { DataStream = crashData, LogStream = crashLog, Password = password });
            using var recovered = new LiteDatabase(recoveredEngine, disposeOnClose: false);
            var rows = recovered.GetCollection("rows");
            rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(1, 2);
            rows.FindById(1)["payload"].AsString.Should().Be("before");
            rows.FindById(2)["payload"].AsString.Should().Be(new string('x', 20000));
        }
    }
}
