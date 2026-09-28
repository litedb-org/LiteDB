using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Regressions;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class WalDurability_Tests
    {
        private sealed class DurableFile : FileStream
        {
            public int DurableFlushes { get; private set; }
            public Exception Failure { get; set; }
            public int FailuresRemaining { get; set; } = int.MaxValue;
            public ManualResetEventSlim FailureObserved { get; set; }
            public Action BeforeFailure { get; set; }

            public DurableFile(string path)
                : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
            {
            }

            public override void Flush(bool flushToDisk)
            {
                base.Flush(flushToDisk);
                if (!flushToDisk) return;
                DurableFlushes++;
                if (Failure != null && FailuresRemaining-- > 0)
                {
                    FailureObserved?.Set();
                    BeforeFailure?.Invoke();
                    throw Failure;
                }
            }
        }

        [Fact]
        public async Task FailedConfirmedFlush_DoesNotDeadlockPartialReclamation()
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            var data = new DurableFile(dataFile.Filename);
            var log = new DurableFile(logFile.Filename);
            var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, TransactionPageLimit = 1000
            });
            var database = new LiteDatabase(engine, disposeOnClose: false);
            database.CheckpointSize = 0;
            var rows = database.GetCollection("rows");
            BsonDocument[] Documents(int value) => Enumerable.Range(0, 32).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1000) }).ToArray();

            rows.Insert(Documents(0));
            for (var value = 1; value <= 5; value++) rows.Update(Documents(value));
            using var reader = engine.Query("rows", new Query());
            engine.Checkpoint();
            for (var value = 6; value <= 8; value++) rows.Update(Documents(value));

            using var writerReady = new ManualResetEventSlim();
            using var commitWriter = new ManualResetEventSlim();
            using var checkpointAtCommitLock = new ManualResetEventSlim();
            using var continueFailure = new ManualResetEventSlim();
            using var flushFailed = new ManualResetEventSlim();
            engine.CheckpointStage = stage =>
            {
                if (stage == "before-commit-lock") checkpointAtCommitLock.Set();
            };

            var writer = Task.Run<Exception>(() =>
            {
                try
                {
                    database.BeginTrans();
                    rows.Update(Documents(9));
                    writerReady.Set();
                    commitWriter.Wait();
                    database.Commit();
                    return null;
                }
                catch (Exception ex) { return ex; }
            });
            writerReady.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            log.Failure = new IOException("injected confirmed flush failure");
            log.FailuresRemaining = 1;
            log.FailureObserved = flushFailed;
            log.BeforeFailure = () => continueFailure.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            commitWriter.Set();
            flushFailed.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var checkpoint = Task.Run(() => engine.Checkpoint());
            try
            {
                checkpointAtCommitLock.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                checkpoint.IsCompleted.Should().BeFalse("checkpoint must exclude in-flight commit publication");
            }
            finally { continueFailure.Set(); }

            var both = Task.WhenAll(checkpoint.ContinueWith(_ => { }), writer.ContinueWith(_ => { }));
            (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(both);
            (await writer).Should().BeOfType<IOException>();
            checkpoint.IsFaulted.Should().BeTrue("failed commit teardown stops the waiting checkpoint");
            engine.CheckpointStage = null;
            database.Dispose();
            engine.Dispose();
            data.Dispose();
            log.Dispose();

            using var recoveredData = new FileStream(dataFile.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            using var recoveredLog = new FileStream(logFile.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            using var recoveredEngine = new LiteEngine(new EngineSettings
            {
                DataStream = recoveredData, LogStream = recoveredLog
            });
            using var recovered = new LiteDatabase(recoveredEngine, disposeOnClose: false);
            recovered.GetCollection("rows").FindAll().Should().HaveCount(32)
                .And.OnlyContain(document => document["value"].AsInt32 == 9);
        }

        /// <summary>
        /// A commit whose durable log flush fails throws that failure, whatever its type, and stops the
        /// engine. The failure is recorded (decision 6 of docs/decisions/durability-policy.md): the next
        /// call reopens the engine read-only from the files as they are. It reads them (the failed
        /// commit's frames reached the file before its flush failed, so its outcome is unknown to the
        /// caller and the files hold it), Rollback completes the explicit transaction the failure ended,
        /// and every later write throws with the record before it changes the files; the read-only
        /// engine never flushes.
        /// </summary>
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
            var flushes = log.DurableFlushes;
            var files = (Data: SyncPowerLossModel.ReadShared(dataFile.Filename), Log: SyncPowerLossModel.ReadShared(logFile.Filename));
            db.Rollback().Should().Be(explicitTransaction, "Rollback completes the explicit transaction the failure ended");
            rows.FindAll().Should().BeEquivalentTo(new[] { new BsonDocument { ["_id"] = 1, ["value"] = "after" } });
            var record = ReadOnlyAfterWriteFailure.AssertReported(db, "A commit's log flush", "log",
                nonIoFailure ? "WAL durable flush failed." : "flush failed");
            Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 2 });
            ReadOnlyAfterWriteFailure.AssertWriteRefused(nextWrite, record)
                .InnerException.Should().BeSameAs(failure, "the refusal carries the recorded failure");
            if (explicitTransaction)
            {
                db.BeginTrans().Should().BeTrue();
                ReadOnlyAfterWriteFailure.AssertWriteRefused(write, record);
                db.Rollback().Should().BeTrue();
            }
            rows.Count().Should().Be(1);
            SyncPowerLossModel.ReadShared(dataFile.Filename).Should().Equal(files.Data, "neither the read-only reopen nor a refused write changes the files");
            SyncPowerLossModel.ReadShared(logFile.Filename).Should().Equal(files.Log);
            log.DurableFlushes.Should().Be(flushes, "the read-only engine never flushes");
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
