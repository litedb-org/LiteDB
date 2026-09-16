using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class WalDurability_Tests
    {
        private sealed class DurableFile : FileStream
        {
            public int DurableFlushes { get; private set; }
            public Exception Failure { get; set; }

            public DurableFile(string path)
                : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
            {
            }

            public override void Flush(bool flushToDisk)
            {
                base.Flush(flushToDisk);
                if (!flushToDisk) return;
                DurableFlushes++;
                if (Failure != null) throw Failure;
            }
        }

        [Theory]
        [InlineData(false, false, null)]
        [InlineData(false, true, null)]
        [InlineData(true, false, null)]
        [InlineData(true, true, null)]
        [InlineData(false, false, "secret")]
        [InlineData(false, true, "secret")]
        [InlineData(true, false, "secret")]
        [InlineData(true, true, "secret")]
        public void Failed_durable_flush_stops_explicit_and_automatic_transactions(
            bool explicitTransaction, bool nonIoFailure, string password)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new DurableFile(dataFile.Filename);
            using var log = new DurableFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            db.Checkpoint();
            if (explicitTransaction) db.BeginTrans();
            log.Failure = nonIoFailure ? new InvalidOperationException("flush failed") : new IOException("flush failed");
            Action write = () =>
            {
                rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" });
                if (explicitTransaction) db.Commit();
            };
            var failure = write.Should().Throw<IOException>().Which;
            if (nonIoFailure) failure.InnerException.Should().BeSameAs(log.Failure);
            else failure.Should().BeSameAs(log.Failure);
            Action rollback = () => db.Rollback();
            rollback.Should().Throw<IOException>();
            Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 2 });
            nextWrite.Should().Throw<IOException>();
            log.Failure = null;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Update_confirmation_is_durable_and_empty_commits_do_not_flush(bool forceSafepoint)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new DurableFile(dataFile.Filename);
            using var log = new DurableFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, TransactionPageLimit = 1
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
            var before = log.DurableFlushes;
            db.BeginTrans();
            db.Commit();
            log.DurableFlushes.Should().Be(before);
            db.BeginTrans();
            rows.Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" });
            if (forceSafepoint) engine.GetMonitor().GetTransactionsSnapshot().Single().Safepoint();
            log.DurableFlushes.Should().Be(before, "unconfirmed safepoints must not fsync");
            db.Commit();
            log.DurableFlushes.Should().Be(before + 1);
            rows.FindById(1)["value"].AsString.Should().Be("after");
        }
    }
}
