using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.WalIdentityCrash_Tests;

namespace LiteDB.Tests.Engine
{
    public class WalIdentityDurableTail_Tests
    {
        private sealed class DurableFile : FileStream
        {
            internal byte[] Durable = Array.Empty<byte>();
            internal int DurableFlushes;
            internal int FailAt = -1;

            internal DurableFile(string path) : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite) { }

            public override void Flush() => base.Flush(false);

            public override void Flush(bool flushToDisk)
            {
                base.Flush(flushToDisk);
                if (!flushToDisk) return;
                var position = Position;
                Position = 0;
                using (var copy = new MemoryStream())
                {
                    CopyTo(copy);
                    Durable = copy.ToArray();
                }
                Position = position;
                if (++DurableFlushes == FailAt) throw new IOException("persisted generation interrupted");
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Completed_fingerprint_never_depends_on_an_unflushed_rollback_tail(string password)
        {
            using var dataPath = new TempFile();
            using var logPath = new TempFile();
            using var data = new DurableFile(dataPath.Filename);
            using var log = new DurableFile(logPath.Filename);
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, TransactionPageLimit = 1
            }))
            using (var db = new LiteDatabase(engine))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                var committedLogLength = log.Durable.Length;
                var flushes = log.DurableFlushes;
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["receipt"] = "rollback!" });
                engine.GetMonitor().GetThreadTransaction().Safepoint();
                db.Rollback().Should().BeTrue();
                log.Length.Should().BeGreaterThan(committedLogLength);
                log.DurableFlushes.Should().Be(flushes, "ordinary safepoint flush is not durable");
                log.Durable.Length.Should().Be(committedLogLength);

                // Both data pages and the next generation persist; the process
                // then exits before WAL truncation. Only fsynced bytes survive.
                data.FailAt = data.DurableFlushes + 2;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("persisted generation interrupted");
                log.DurableFlushes.Should().Be(flushes + 1);
            }

            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            using var recovered = Open(recoveredData, recoveredLog, password);
            AssertRows(recovered, 1);
        }
    }
}
