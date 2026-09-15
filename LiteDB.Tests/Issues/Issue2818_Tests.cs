using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2818_Tests
    {
        private sealed class GatedDurableFile : FileStream
        {
            private readonly object _snapshotLock = new object();
            private readonly ManualResetEventSlim _releaseDurableFlush = new ManualResetEventSlim(false);
            private TaskCompletionSource<bool> _durableFlushEntered = CreateSignal();
            private byte[] _durableBytes = new byte[0];
            private int _durableFlushes;
            private int _gateArmed;

            public GatedDurableFile(string path)
                : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
            {
            }

            public byte[] DurableBytes
            {
                get
                {
                    lock (_snapshotLock)
                    {
                        return (byte[])_durableBytes.Clone();
                    }
                }
            }

            public int DurableFlushes => Volatile.Read(ref _durableFlushes);

            public Task DurableFlushEntered => Volatile.Read(ref _durableFlushEntered).Task;

            public void ArmDurableFlushGate()
            {
                _releaseDurableFlush.Reset();
                Volatile.Write(ref _durableFlushEntered, CreateSignal());
                Volatile.Write(ref _gateArmed, 1);
            }

            public void ReleaseDurableFlush()
            {
                Volatile.Write(ref _gateArmed, 0);
                _releaseDurableFlush.Set();
            }

            public override void Flush(bool flushToDisk)
            {
                if (!flushToDisk)
                {
                    base.Flush(false);
                    return;
                }

                if (Volatile.Read(ref _gateArmed) != 0)
                {
                    Volatile.Read(ref _durableFlushEntered).TrySetResult(true);
                    _releaseDurableFlush.Wait();
                }

                base.Flush(true);

                var position = Position;
                Position = 0;

                using (var copy = new MemoryStream())
                {
                    CopyTo(copy);

                    lock (_snapshotLock)
                    {
                        _durableBytes = copy.ToArray();
                    }
                }

                Position = position;
                Interlocked.Increment(ref _durableFlushes);
            }

            private static TaskCompletionSource<bool> CreateSignal()
            {
                return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        [Fact]
        public async Task Durable_flush_gate_publishes_bytes_only_after_the_flush_is_released()
        {
            using var file = new TempFile();
            using var stream = new GatedDurableFile(file.Filename);
            var firstWrite = new byte[] { 1, 2, 3 };
            var secondWrite = new byte[] { 4, 5, 6 };

            stream.Write(firstWrite, 0, firstWrite.Length);
            stream.Flush(true);
            stream.DurableBytes.Should().Equal(firstWrite);

            stream.Write(secondWrite, 0, secondWrite.Length);
            stream.ArmDurableFlushGate();
            var flushTask = Task.Run(() => stream.Flush(true));

            try
            {
                var gateTask = stream.DurableFlushEntered;
                var firstTask = await Task.WhenAny(gateTask, Task.Delay(TimeSpan.FromSeconds(10)));
                firstTask.Should().BeSameAs(gateTask,
                    "the healthy-control flush must reach the explicit synchronization gate");
                flushTask.IsCompleted.Should().BeFalse(
                    "Flush(true) is held inside the stream until the test releases it");
                stream.DurableBytes.Should().Equal(firstWrite,
                    "bytes behind an unreleased durable flush must not be published as durable");
            }
            finally
            {
                stream.ReleaseDurableFlush();
            }

            var completedTask = await Task.WhenAny(flushTask, Task.Delay(TimeSpan.FromSeconds(10)));
            completedTask.Should().BeSameAs(flushTask);
            await flushTask;
            stream.DurableBytes.Should().Equal(firstWrite.Concat(secondWrite));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Commit_waits_for_log_fsync_and_durable_snapshot_recovers_exact_ledger(string password)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new GatedDurableFile(dataFile.Filename);
            using var log = new GatedDurableFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings
            {
                DataStream = data,
                LogStream = log,
                Password = password
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;

            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "before-update" });
            rows.Insert(new BsonDocument { ["_id"] = 3, ["payload"] = "to-be-deleted" });
            db.Checkpoint();

            var insertedPayload = "committed-" + new string('x', 20000);
            var flushesBeforeCommit = log.DurableFlushes;
            var durableLogBeforeCommit = log.DurableBytes;
            log.ArmDurableFlushGate();
            var durableFlushEntered = log.DurableFlushEntered;
            var commitTask = Task.Run(() =>
            {
                // Transactions are thread-bound. Keep BeginTrans, every write and
                // Commit on this worker while the test thread observes the gate.
                db.BeginTrans().Should().BeTrue();
                rows.Update(new BsonDocument { ["_id"] = 1, ["payload"] = "after-update" }).Should().BeTrue();
                rows.Insert(new BsonDocument { ["_id"] = 2, ["payload"] = insertedPayload });
                rows.Delete(3).Should().BeTrue();

                return db.Commit();
            });
            Task firstSignal;
            bool commitCompletedWhileFlushWasHeld;
            byte[] durableLogWhileFlushWasHeld;

            try
            {
                firstSignal = await Task.WhenAny(
                    durableFlushEntered,
                    commitTask,
                    Task.Delay(TimeSpan.FromSeconds(10)));
                commitCompletedWhileFlushWasHeld = commitTask.IsCompleted;
                durableLogWhileFlushWasHeld = log.DurableBytes;
            }
            finally
            {
                log.ReleaseDurableFlush();
            }

            var completedCommit = await Task.WhenAny(commitTask, Task.Delay(TimeSpan.FromSeconds(10)));
            completedCommit.Should().BeSameAs(commitTask,
                "releasing the stream gate must let the commit finish");
            firstSignal.Should().BeSameAs(durableFlushEntered,
                "the log's Flush(true) must begin before Commit() is allowed to return");
            commitCompletedWhileFlushWasHeld.Should().BeFalse(
                "Commit() must synchronously wait for the forced log flush");
            durableLogWhileFlushWasHeld.Should().Equal(durableLogBeforeCommit,
                "the transaction bytes are not durable while Flush(true) is still blocked");
            (await commitTask).Should().BeTrue();
            log.DurableFlushes.Should().BeGreaterThan(flushesBeforeCommit);

            data.DurableBytes.Should().NotBeEmpty(
                "the checkpointed half of the crash image must itself have reached durable storage");
            log.DurableBytes.Should().NotBeEmpty(
                "the committed WAL half of the crash image must have reached durable storage");

            using var crashData = ExpandableCopy(data.DurableBytes);
            using var crashLog = ExpandableCopy(log.DurableBytes);
            using var recoveredEngine = new LiteEngine(new EngineSettings
            {
                DataStream = crashData,
                LogStream = crashLog,
                Password = password
            });
            using var recovered = new LiteDatabase(recoveredEngine, disposeOnClose: false);
            var recoveredRows = recovered.GetCollection("rows");

            recoveredRows.FindAll()
                .Select(x => x["_id"].AsInt32)
                .OrderBy(x => x)
                .Should().Equal(1, 2);
            recoveredRows.FindById(1)["payload"].AsString.Should().Be("after-update");
            recoveredRows.FindById(2)["payload"].AsString.Should().Be(insertedPayload);
            Assert.Null(recoveredRows.FindById(3));
        }

        private static MemoryStream ExpandableCopy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }
    }
}
