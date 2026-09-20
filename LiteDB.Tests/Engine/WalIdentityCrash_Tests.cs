using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class WalIdentityCrash_Tests
    {
        internal sealed class CrashStream : MemoryStream
        {
            internal byte[] Durable = Array.Empty<byte>();
            internal int FlushCount;
            internal int FailFlushAt = -1;
            internal bool PersistFailedFlush;
            internal bool FailTruncate;
            internal bool UnsupportedTruncate;
            internal int WritesBeforeDenied = -1;
            internal bool TearNextWrite;
            private bool _powerLost;
            internal long TruncateLength;
            internal readonly IOException Failure = new IOException("injected checkpoint interruption");

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (TearNextWrite)
                {
                    TearNextWrite = false;
                    base.Write(buffer, offset, Math.Min(count, 256));
                    Durable = this.ToArray();
                    _powerLost = true;
                    throw Failure;
                }
                base.Write(buffer, offset, count);
                if (WritesBeforeDenied >= 0 && WritesBeforeDenied-- == 0)
                {
                    Durable = this.ToArray();
                    throw new UnauthorizedAccessException("injected persisted metadata write failure");
                }
            }

            public override void Flush()
            {
                var fail = ++FlushCount == FailFlushAt;
                if (!_powerLost && (!fail || PersistFailedFlush)) Durable = this.ToArray();
                if (fail) throw Failure;
            }

            public override void SetLength(long value)
            {
                if (value == TruncateLength && UnsupportedTruncate) throw new NotSupportedException("injected unsupported truncation");
                if (value == TruncateLength && FailTruncate) throw Failure;
                base.SetLength(value);
            }
        }

        [Theory]
        [InlineData(null, "data", false)]
        [InlineData(null, "data", true)]
        [InlineData(null, "wal", false)]
        [InlineData(null, "wal", true)]
        [InlineData(null, "identity", false)]
        [InlineData(null, "identity", true)]
        [InlineData(null, "truncate", false)]
        [InlineData(null, "truncate-flush", false)]
        [InlineData(null, "truncate-flush", true)]
        [InlineData("secret", "data", false)]
        [InlineData("secret", "data", true)]
        [InlineData("secret", "wal", false)]
        [InlineData("secret", "wal", true)]
        [InlineData("secret", "identity", false)]
        [InlineData("secret", "identity", true)]
        [InlineData("secret", "truncate", false)]
        [InlineData("secret", "truncate-flush", false)]
        [InlineData("secret", "truncate-flush", true)]
        public void Every_checkpoint_interruption_preserves_acknowledged_rows(string password, string phase, bool persisted)
        {
            using var data = new CrashStream();
            using var log = new CrashStream();
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.Checkpoint(); // Warm encrypted readers before arming flush boundaries.
                db.GetCollection("rows").Insert(new[] { Row(1), Row(2) });
                log.TruncateLength = password == null ? 0 : Constants.PAGE_SIZE;
                data.PersistFailedFlush = log.PersistFailedFlush = persisted;
                if (phase == "data") data.FailFlushAt = data.FlushCount + 1;
                if (phase == "identity") data.FailFlushAt = data.FlushCount + 2;
                if (phase == "truncate") log.FailTruncate = true;
                if (phase == "wal") log.FailFlushAt = log.FlushCount + 1;
                if (phase == "truncate-flush") log.FailFlushAt = log.FlushCount + 2;
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("injected checkpoint interruption");
                Action laterWrite = () => db.GetCollection("rows").Insert(Row(99));
                laterWrite.Should().Throw<IOException>().WithMessage("*injected checkpoint interruption");
            }

            // Only flushed bytes survive this simulated power loss. Read-only
            // recovery must also leave a completed-but-untruncated WAL untouched.
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            using (var reader = Open(recoveredData, recoveredLog, password, readOnly: true))
                AssertRows(reader, 1, 2);
            recoveredData.ToArray().Should().Equal(data.Durable);
            recoveredLog.ToArray().Should().Equal(log.Durable);
            using (var writer = Open(recoveredData, recoveredLog, password))
            {
                AssertRows(writer, 1, 2);
                writer.GetCollection("rows").Insert(Row(3));
                writer.Checkpoint();
            }
            using (var reopened = Open(recoveredData, recoveredLog, password))
                AssertRows(reopened, 1, 2, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Saved_subset_of_completed_WAL_cannot_overwrite_later_commits(string password)
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            byte[] oldLog;
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                oldLog = log.ToArray();
                db.GetCollection("rows").Insert(Row(2));
                db.Checkpoint();
            }
            using var restoredLog = Copy(oldLog);
            var before = data.ToArray();
            Action open = () => { using var db = Open(data, restoredLog, password); };
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.INVALID_WAL);
            data.ToArray().Should().Equal(before);
            restoredLog.ToArray().Should().Equal(oldLog);
            using var emptyLog = new MemoryStream();
            using var checkpointed = Open(data, emptyLog, password);
            AssertRows(checkpointed, 1, 2);
        }

        [Theory]
        [InlineData(null, "data")]
        [InlineData(null, "wal")]
        [InlineData(null, "identity")]
        [InlineData(null, "truncate")]
        [InlineData(null, "truncate-flush")]
        [InlineData("secret", "data")]
        [InlineData("secret", "wal")]
        [InlineData("secret", "identity")]
        [InlineData("secret", "truncate")]
        [InlineData("secret", "truncate-flush")]
        public void Explicit_commit_stops_after_its_automatic_checkpoint_fails(string password, string phase)
        {
            using var data = new CrashStream();
            using var log = new CrashStream();
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.Checkpoint();
                db.GetCollection("rows").Insert(new[] { Row(1), Row(2) });
                db.CheckpointSize = 1;
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("rows").Insert(Row(3));
                data.PersistFailedFlush = log.PersistFailedFlush = true;
                log.TruncateLength = password == null ? 0 : Constants.PAGE_SIZE;
                if (phase == "data") data.FailFlushAt = data.FlushCount + 1;
                if (phase == "identity") data.FailFlushAt = data.FlushCount + 2;
                if (phase == "truncate") log.FailTruncate = true;
                // Commit and fingerprint preparation each durably flush the WAL.
                if (phase == "wal") log.FailFlushAt = log.FlushCount + 2;
                if (phase == "truncate-flush") log.FailFlushAt = log.FlushCount + 3;
                Action commit = () => db.Commit();
                commit.Should().Throw<IOException>().WithMessage("injected checkpoint interruption");
                Action laterWrite = () => db.GetCollection("rows").Insert(Row(99));
                laterWrite.Should().Throw<IOException>().WithMessage("*injected checkpoint interruption");
            }
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            using var recovered = Open(recoveredData, recoveredLog, password);
            AssertRows(recovered, 1, 2, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Unsupported_truncation_stops_the_engine_before_later_commits(string password)
        {
            using var data = new CrashStream();
            using var log = new CrashStream();
            using (var db = Open(data, log, password))
            {
                db.CheckpointSize = 0;
                db.Checkpoint();
                db.GetCollection("rows").Insert(Row(1));
                log.TruncateLength = password == null ? 0 : Constants.PAGE_SIZE;
                log.UnsupportedTruncate = true;
                Action checkpoint = () => db.Checkpoint();
                var failure = checkpoint.Should().Throw<IOException>().Which;
                failure.InnerException.Should().BeOfType<NotSupportedException>();
                Action laterWrite = () => db.GetCollection("rows").Insert(Row(99));
                laterWrite.Should().Throw<IOException>().Which.InnerException.Should().BeSameAs(failure);
            }
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            using var recovered = Open(recoveredData, recoveredLog, password);
            AssertRows(recovered, 1);
        }

        [Fact]
        public void Non_IO_identity_write_failure_stops_the_engine()
        {
            using var data = new CrashStream();
            using var log = new CrashStream();
            using (var db = Open(data, log))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Row(1));
                // Checkpoint writes one data page per confirmed WAL frame, then
                // separately publishes the new generation in the data header.
                data.WritesBeforeDenied = (int)(log.Length / Constants.PAGE_SIZE) - 1;
                Action checkpoint = () => db.Checkpoint();
                var failure = checkpoint.Should().Throw<IOException>().Which;
                failure.InnerException.Should().BeOfType<UnauthorizedAccessException>();
                Action laterWrite = () => db.GetCollection("rows").Insert(Row(99));
                laterWrite.Should().Throw<IOException>().Which.InnerException.Should().BeSameAs(failure);
            }
            using var recoveredData = Copy(data.Durable);
            using var recoveredLog = Copy(log.Durable);
            using var recovered = Open(recoveredData, recoveredLog);
            AssertRows(recovered, 1);
        }

        internal static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            return stream;
        }

        internal static LiteDatabase Open(Stream data, Stream log, string password = null, bool readOnly = false) =>
            new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly
            }));

        internal static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["receipt"] = "receipt-" + id };

        internal static void AssertRows(LiteDatabase db, params int[] ids)
        {
            var rows = db.GetCollection("rows").FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            rows.Select(row => row["_id"].AsInt32).Should().Equal(ids);
            rows.Select(row => row["receipt"].AsString).Should().Equal(ids.Select(id => "receipt-" + id));
        }
    }
}
