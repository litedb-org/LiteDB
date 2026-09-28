using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #2818: what a commit does when the log's storage rejects a device sync (#2242) or fails one.
    /// Without durable commits a rejection is not a failure and commits flush to the OS; with them a
    /// commit that cannot be made durable throws and the failure is sticky
    /// (docs/decisions/durability-policy.md, decisions 3 and 6); a real failure stops writes in both.
    /// </summary>
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
            // Plain flushes after a durable flush failed: a fallback that papers over the failure.
            public int PlainFlushesAfterDurableFailure { get; private set; }
            private bool _durableFailed;
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
                    _durableFailed = DurableFailure != null;
                    if (DurableFailure != null) throw DurableFailure;
                    base.Flush(true);
                }
                else
                {
                    PlainFlushes++;
                    if (_durableFailed) PlainFlushesAfterDurableFailure++;
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

        /// <summary>
        /// Opted out of durable commits (proposed default A of docs/decisions/durability-policy.md), a
        /// log whose storage rejects a device sync is not a failure: commits flush to the OS only, as
        /// they always do without durable commits, and a checkpoint that asks for a device sync and is
        /// rejected proceeds in write order. The weaker guarantee is discoverable, nothing is recorded.
        /// </summary>
        [Theory]
        [MemberData(nameof(UnsupportedDurableFlush))]
        public void Commit_flushes_to_the_os_when_storage_rejects_durable_flush_without_durable_commits(
            Exception rejection, string password)
        {
            using var dataFile = new TempFile();
            using var logFile = new TempFile();
            using var data = new ScriptedFlushFile(dataFile.Filename);
            using var log = new ScriptedFlushFile(logFile.Filename);

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password, DurableCommits = false }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                // The second insert reads the log, so the pooled log reader exists before the
                // rejection is armed: opening an encrypted stream issues its own durable flush.
                rows.Insert(new BsonDocument { ["_id"] = 0 });
                rows.Insert(new BsonDocument { ["_id"] = 1 });

                var durableBefore = log.DurableFlushes;
                var plainBefore = log.PlainFlushes;
                log.DurableFailure = rejection;

                rows.Insert(new BsonDocument { ["_id"] = 2 });
                rows.Insert(new BsonDocument { ["_id"] = 3 });
                db.BeginTrans();
                rows.Insert(new BsonDocument { ["_id"] = 4 });
                db.Commit();

                log.DurableFlushes.Should().Be(durableBefore, "an opted-out commit never asks for a device sync");
                log.PlainFlushes.Should().BeGreaterThanOrEqualTo(plainBefore + 3,
                    "every commit still flushes the log to the OS");

                db.Checkpoint();
                log.DurableFlushes.Should().BeGreaterThan(durableBefore, "a checkpoint still tries a real sync");
                rows.Insert(new BsonDocument { ["_id"] = 5 });

                DurableLogFlush(db).Should().BeFalse("the degradation must be discoverable");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
                rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(0, 1, 2, 3, 4, 5);
            }

            log.DurableFailure = null;

            using (var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false))
            {
                reopened.GetCollection("rows").FindAll()
                    .Select(x => x["_id"].AsInt32)
                    .OrderBy(x => x)
                    .Should().Equal(0, 1, 2, 3, 4, 5);
            }
        }

        /// <summary>
        /// With durable commits (the default) a log that stops syncing after the proof before the
        /// engine's first commit fails the commit that finds out (decision 3, implementation note 4):
        /// its frames already reached the operating system, so its error says the outcome is unknown.
        /// It used to be acknowledged, degraded to a plain flush. The failure is sticky (decision 6): no
        /// further device sync is asked, reads return the files as they are (the commit's frames reached
        /// the OS, so it is there), $database reports it, and the next write throws with it.
        /// </summary>
        [Theory]
        [MemberData(nameof(UnsupportedDurableFlush))]
        public void Commit_fails_with_an_unknown_outcome_when_storage_starts_rejecting_durable_flush(
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
                log.DurableFailure = rejection;

                Action insert = () => rows.Insert(new BsonDocument { ["_id"] = 2 });
                insert.Should().Throw<IOException>().WithMessage(WriteFailureAssert.OutcomeUnknown + "*")
                    .Which.InnerException.Should().BeSameAs(rejection);
                log.DurableFlushes.Should().Be(durableBefore + 1);

                rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(0, 1, 2);
                var reason = WriteFailureAssert.Recorded(db, "A commit's log flush", "log", WriteFailureAssert.OutcomeUnknown, walKept: true);
                var dataNow = TempFile.ReadAllBytesShared(dataFile.Filename);
                var logNow = TempFile.ReadAllBytesShared(logFile.Filename);
                WriteFailureAssert.Refused(() => rows.Insert(new BsonDocument { ["_id"] = 3 }), reason);
                log.DurableFlushes.Should().Be(durableBefore + 1, "a refused write asks the storage nothing");
                TempFile.ReadAllBytesShared(dataFile.Filename).Should().Equal(dataNow);
                TempFile.ReadAllBytesShared(logFile.Filename).Should().Equal(logNow);
                rows.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(0, 1, 2);
            }

            log.DurableFailure = null;

            using (var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, Password = password }))
            using (var reopened = new LiteDatabase(reopenedEngine, disposeOnClose: false))
            {
                reopened.GetCollection("rows").FindAll()
                    .Select(x => x["_id"].AsInt32)
                    .OrderBy(x => x)
                    .Should().Equal(0, 1, 2);
                WriteFailureAssert.NoneRecorded(reopened, "a reopen retries");
                reopened.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3 });
                DurableLogFlush(reopened).Should().BeTrue();
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
            log.DurableFailure = new IOException("There is not enough space on the disk.", _diskFull);

            Action write = () => rows.Insert(new BsonDocument { ["_id"] = 2 });
            Action nextWrite = () => rows.Insert(new BsonDocument { ["_id"] = 3 });

            write.Should().Throw<IOException>().Which.Should().BeSameAs(log.DurableFailure);
            // Each frame write is flushed to the OS before the durable flush; none may follow its failure.
            log.PlainFlushesAfterDurableFailure.Should().Be(0, "an I/O failure must not be papered over by a weaker flush");
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
