using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class SharedCommitFailure_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
            foreach (var storage in new[] { CompactStorageMode.Legacy, CompactStorageMode.Auto })
            foreach (var explicitTransaction in new[] { false, true })
            foreach (var fault in new[] { "none", "lost", "torn", "after-sync" })
                yield return new object[] { password, storage, explicitTransaction, fault };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void SharedCommit_DeviceFailuresRecoverWholeTransactionsAndReleaseWriterOwnership(
            string password, CompactStorageMode storage, bool explicitTransaction, string fault)
        {
            using var source = new TempFile();
            using var data = new DeviceFile(source.Filename);
            using var log = new DeviceFile(source.Filename + "-wal");
            using var shared = new SharedEngine(new EngineSettings
            {
                Filename = source.Filename, DataStream = data, LogStream = log,
                Password = password, CompactStorage = storage, TransactionPageLimit = 1
            });
            using var db = new LiteDatabase(shared, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Documents(0));
            rows.EnsureIndex("value");
            db.GetCollection("cold").Insert(Documents(42));
            db.Checkpoint();
            rows.Update(Documents(1)).Should().Be(16);
            var preamble = password == null ? 0 : PAGE_SIZE;
            var acknowledged = (byte[])log.Durable.Clone();
            var confirmedEnd = preamble + (acknowledged.Length - preamble) / WalChecksum.FrameSize * WalChecksum.FrameSize;
            if (explicitTransaction) db.BeginTrans().Should().BeTrue();
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage == "wal-before-durable-flush" && fault != "none")
                {
                    log.Fault = fault;
                    log.TearPosition = confirmedEnd + WalChecksum.FrameSize + 128;
                }
            };
            try
            {
                Action commit = () =>
                {
                    rows.Update(Documents(2)).Should().Be(16);
                    if (explicitTransaction) db.Commit().Should().BeTrue();
                };
                if (fault == "none") commit();
                else commit.Should().Throw<IOException>().WithMessage("simulated device failure");
            }
            finally { EngineState.SimulateProcessCrash = null; }

            log.Failures.Should().Be(fault == "none" ? 0 : 1);
            log.Durable.Take(confirmedEnd).Should().Equal(acknowledged.Take(confirmedEnd),
                "a failure of a new append must preserve the previously acknowledged WAL prefix");
            var expected = fault == "none" || fault == "after-sync" ? 2 : 1;
            RecoverTwice(data.Durable, log.Durable, password, storage, expected);

            // A failed automatic or explicit commit must release the shared
            // mutex. Acquire it from another thread without modifying the source.
            MvccCheckpoint_Tests.RunThread(() =>
            {
                using var mutex = SharedMutexFactory.Create(SharedMutexNameFactory.Create(
                    source.Filename, SharedMutexNameStrategy.Default));
                mutex.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
                mutex.ReleaseMutex();
            });
        }

        private static void RecoverTwice(byte[] data, byte[] log, string password, CompactStorageMode storage, int expected)
        {
            using var recovered = new TempFile();
            var wal = FileHelper.GetLogFile(recovered.Filename);
            File.WriteAllBytes(recovered.Filename, data);
            File.WriteAllBytes(wal, log);
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var db = new LiteDatabase(new ConnectionString
                    {
                        Filename = recovered.Filename, Connection = ConnectionType.Shared,
                        Password = password, CompactStorage = storage
                    });
                    var rows = db.GetCollection("rows");
                    rows.FindAll().Should().BeEquivalentTo(Documents(expected));
                    var indexed = rows.Query().Where(Query.EQ("value", expected));
                    indexed.GetPlan()["index"]["name"].AsString.Should().Be("value");
                    indexed.ToArray().Should().BeEquivalentTo(Documents(expected));
                    rows.Find(Query.EQ("value", expected == 1 ? 2 : 1)).Should().BeEmpty();
                    db.GetCollection("cold").FindAll().Should().BeEquivalentTo(Documents(42));
                    db.Checkpoint();
                }
            }
            finally
            {
                File.Delete(wal);
                if (Directory.Exists(recovered.Filename + "-readers"))
                    Directory.Delete(recovered.Filename + "-readers", recursive: true);
            }
        }

        private static BsonDocument[] Documents(int value) => Enumerable.Range(0, 16).Select(id =>
            new BsonDocument { ["_id"] = id, ["value"] = value,
                ["payload"] = new string((char)('a' + id), 3000) + ":" + value }).ToArray();

        private sealed class DeviceFile : FileStream
        {
            internal byte[] Durable = Array.Empty<byte>();
            internal string Fault;
            internal int TearPosition;
            internal int Failures;
            private bool _crashed;

            internal DeviceFile(string filename)
                : base(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                    4096, FileOptions.DeleteOnClose) { }

            public override void Flush(bool flushToDisk)
            {
                if (!flushToDisk) { base.Flush(false); return; }
                if (_crashed) return; // No later cleanup can change the captured crash image.
                if (Fault != null)
                {
                    Failures++;
                    if (Fault == "torn")
                    {
                        Durable = Snapshot();
                        // New sectors including the confirmation persist, while
                        // part of an earlier new frame is lost. Old ranges survive.
                        Array.Clear(Durable, TearPosition, 512);
                    }
                    else if (Fault == "after-sync")
                    {
                        base.Flush(true);
                        Durable = Snapshot();
                    }
                    _crashed = true;
                    throw new IOException("simulated device failure");
                }
                base.Flush(true);
                Durable = Snapshot();
            }

            private byte[] Snapshot()
            {
                base.Flush(false);
                var position = Position;
                Position = 0;
                var bytes = new byte[checked((int)Length)];
                this.ReadRequired(bytes, 0, bytes.Length);
                Position = position;
                return bytes;
            }
        }
    }
}
