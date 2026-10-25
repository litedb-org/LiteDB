using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedDurability_Tests
    {
        [Theory]
        [InlineData(null, false, false)]
        [InlineData(null, false, true)]
        [InlineData(null, true, false)]
        [InlineData(null, true, true)]
        [InlineData("secret", false, false)]
        [InlineData("secret", false, true)]
        [InlineData("secret", true, false)]
        [InlineData("secret", true, true)]
        public void SharedCommit_HonorsDurabilitySettingAtConfirmation(string password, bool durable, bool explicitTransaction)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            using var log = new SyncFile(file.Filename + "-wal");
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log,
                Password = password, DurableCommits = durable
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            var before = -1;
            var after = -1;
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush") before = log.DurableSyncs;
                if (stage == "wal-after-durable-flush") after = log.DurableSyncs;
            };
            try
            {
                if (explicitTransaction) db.BeginTrans().Should().BeTrue();
                rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" }).Should().BeTrue();
                if (explicitTransaction) db.Commit().Should().BeTrue();
            }
            finally { EngineState.SimulateProcessCrash = null; }
            before.Should().BeGreaterThanOrEqualTo(0);
            after.Should().Be(before + (durable ? 1 : 0));
            IsDurable(db).Should().Be(durable);
            rows.FindById(1)["value"].AsString.Should().Be("after");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void UnsupportedSync_RemainsVisibleAfterSharedEngineReopens(string password)
        {
            using var file = new TempFile();
            using var data = new SyncFile(file.Filename);
            using var log = new SyncFile(file.Filename + "-wal");
            var settings = new EngineSettings
            {
                Filename = file.Filename, DataStream = data, LogStream = log, Password = password
            };
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush")
                    log.Failure = new UnauthorizedAccessException("sync unsupported");
            };
            try { rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" }).Should().BeTrue(); }
            finally
            {
                EngineState.SimulateProcessCrash = null;
                log.Failure = null;
            }
            log.RejectedSyncs.Should().Be(1);

            IsDurable(db).Should().BeFalse("reopening for diagnostics must retain the weaker guarantee");
            var before = -1;
            var after = -1;
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush") before = log.DurableSyncs;
                if (stage == "wal-after-durable-flush") after = log.DurableSyncs;
            };
            try { rows.Insert(new BsonDocument { ["_id"] = 2 }); }
            finally { EngineState.SimulateProcessCrash = null; }
            before.Should().BeGreaterThanOrEqualTo(0);
            after.Should().Be(before + 1, "the new commit must retry device sync, independently of preamble syncs");
            rows.FindById(1)["value"].AsString.Should().Be("after");
            IsDurable(db).Should().BeFalse("the shared connection previously acknowledged a degraded commit");
            db.Checkpoint();
            using var fresh = new LiteDatabase(new SharedEngine(settings));
            IsDurable(fresh).Should().BeTrue("the diagnostic belongs to the connection, not the file or caller settings");
            fresh.GetCollection("rows").Count().Should().Be(2);
        }

        private static bool IsDurable(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private sealed class SyncFile : FileStream
        {
            internal Exception Failure;
            internal int RejectedSyncs;
            internal int DurableSyncs;

            internal SyncFile(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                    4096, FileOptions.DeleteOnClose) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk && Failure != null)
                {
                    RejectedSyncs++;
                    throw Failure;
                }
                base.Flush(flushToDisk);
                if (flushToDisk) DurableSyncs++;
            }
        }
    }
}
