using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2818_FlushFallback_Tests
    {
        // Windows reports HRESULT_FROM_WIN32(code); Unix runtimes report the raw errno.
        private static readonly bool _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly int _invalidFunction = _isWindows ? unchecked((int)0x80070001) : 22; // EINVAL
        private static readonly int _notSupported = _isWindows ? unchecked((int)0x80070032) : 30; // EROFS
        private static readonly int _diskFull = _isWindows ? unchecked((int)0x80070070) : 28; // ENOSPC

        /// <summary>
        /// Stands in for a file on storage whose FlushFileBuffers/fsync answer is scripted.
        /// </summary>
        private sealed class ScriptedFlushFile : FileStream
        {
            public int DurableFlushes { get; private set; }
            public int PlainFlushes { get; private set; }
            public Exception DurableFailure { get; set; }
            public Exception PlainFailure { get; set; }

            public ScriptedFlushFile(string path)
                : base(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite)
            {
            }

            public override void Flush(bool flushToDisk)
            {
                // Like the real FileStream, the buffer reaches the OS before the OS is asked to sync.
                base.Flush(false);

                if (flushToDisk)
                {
                    DurableFlushes++;
                    if (DurableFailure != null) throw DurableFailure;
                    base.Flush(true);
                }
                else
                {
                    PlainFlushes++;
                    if (PlainFailure != null) throw PlainFailure;
                }
            }
        }

        public static TheoryData<Exception, string> UnsupportedDurableFlush()
        {
            var data = new TheoryData<Exception, string>();

            foreach (var password in new[] { null, "secret" })
            {
                // #2242: FlushFileBuffers answers ERROR_ACCESS_DENIED on some network shares.
                data.Add(new UnauthorizedAccessException("Access to the path is denied."), password);
                data.Add(new IOException("Incorrect function.", _invalidFunction), password);
                data.Add(new IOException("The request is not supported.", _notSupported), password);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(UnsupportedDurableFlush))]
        public void Commit_degrades_once_to_a_plain_flush_when_storage_rejects_durable_flush(
            Exception rejection, string password)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new ScriptedFlushFile(dataFile.Filename);
            using var log = new ScriptedFlushFile(logFile.Filename);

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                // The second insert reads the log, so the pooled log reader exists before the
                // rejection is armed: opening an encrypted stream issues its own durable flush.
                rows.Insert(new BsonDocument { ["_id"] = 0 });
                rows.Insert(new BsonDocument { ["_id"] = 1 });
                DurableLogFlush(db).Should().BeTrue("a durable flush that works must keep being used");

                var durableBefore = log.DurableFlushes;
                var plainBefore = log.PlainFlushes;
                log.DurableFailure = rejection;

                rows.Insert(new BsonDocument { ["_id"] = 2 });
                rows.Insert(new BsonDocument { ["_id"] = 3 });
                db.BeginTrans();
                rows.Insert(new BsonDocument { ["_id"] = 4 });
                db.Commit();

                log.DurableFlushes.Should().Be(durableBefore + 1,
                    "the rejected durable flush is remembered, not retried on every commit");
                log.PlainFlushes.Should().BeGreaterThanOrEqualTo(plainBefore + 3,
                    "every commit after the rejection still flushes the log to the OS");
                DurableLogFlush(db).Should().BeFalse("the degradation must be discoverable");
                rows.Count().Should().Be(5);
            }

            log.DurableFailure = null;

            using (var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false))
            {
                reopened.GetCollection("rows").FindAll()
                    .Select(x => x["_id"].AsInt32)
                    .OrderBy(x => x)
                    .Should().Equal(0, 1, 2, 3, 4);
            }
        }

        [Fact]
        public void Commit_still_stops_the_engine_when_the_durable_flush_fails_with_disk_full()
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new ScriptedFlushFile(dataFile.Filename);
            using var log = new ScriptedFlushFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            var plainBefore = log.PlainFlushes;
            log.DurableFailure = new IOException("There is not enough space on the disk.", _diskFull);

            Action write = () => rows.Insert(new BsonDocument { ["_id"] = 2 });
            Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 3 });

            write.Should().Throw<IOException>().Which.Should().BeSameAs(log.DurableFailure);
            log.PlainFlushes.Should().Be(plainBefore, "an I/O failure must not be papered over by a weaker flush");
            nextWrite.Should().Throw<IOException>();
            log.DurableFailure = null;
        }

        [Fact]
        public void Commit_stops_the_engine_when_the_fallback_plain_flush_fails_too()
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new ScriptedFlushFile(dataFile.Filename);
            using var log = new ScriptedFlushFile(logFile.Filename);
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            log.DurableFailure = new UnauthorizedAccessException("Access to the path is denied.");
            log.PlainFailure = new IOException("The specified network name is no longer available.");

            Action write = () => rows.Insert(new BsonDocument { ["_id"] = 2 });
            Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 3 });

            write.Should().Throw<IOException>().Which.Should().BeSameAs(log.PlainFailure);
            nextWrite.Should().Throw<IOException>();
            log.DurableFailure = null;
            log.PlainFailure = null;
        }

        private static bool DurableLogFlush(LiteDatabase db)
        {
            return db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;
        }
    }
}
