#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A commit's confirmation frame can reach the log whole although its write throws (a file system
    /// that reports an error after it stored the bytes, a caller stream). The engine truncated the failed
    /// append away and reported the commit "NotCommitted" (decision 14 of
    /// docs/decisions/durability-policy.md), but a truncation is not durable until the log syncs: a power
    /// loss before the next sync could leave the confirmation in the log, and a cold reopen recovered the
    /// commit its caller was told was not committed. The engine now syncs the log after it truncated a
    /// failed confirmation away (on that failure path only, in both commit modes) and reports
    /// "NotCommitted" only once that sync succeeded; when it fails, or the log cannot sync, the outcome
    /// is "Unknown". Either way the engine continues read-only (decision 6). A failure before the
    /// confirmation's write, or of an earlier frame of the commit, stays "NotCommitted" without a sync:
    /// no confirmation of it was written.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class WrittenConfirmation_Tests
    {
        private const int EIO = 5;

        /// <summary>
        /// Caller streams whose device image keeps only synced writes (<see cref="ForgetfulFile"/>). The
        /// update's one frame is its confirmation: it reaches the file, then its write throws, and the
        /// append is truncated away. A power loss may then leave every write up to the confirmation
        /// written back, and not the truncation after it (nor anything later) unless a sync made it durable.
        /// Also with durable commits turned off: a commit reported not committed must not come back.
        /// </summary>
        [Theory]
        [InlineData("syncs", true)]
        [InlineData("eio", true)]
        [InlineData("cannot-sync", true)]
        [InlineData("syncs", false)]
        [InlineData("cannot-sync", false)]
        public void Confirmation_that_reached_the_log_before_its_write_threw_is_not_reported_not_committed(string sync, bool durableCommits)
        {
            using var file = new TempFile();
            using var logFile = new TempFile();
            using var data = new ForgetfulFile(file.Filename);
            using var log = new ForgetfulFile(logFile.Filename);
            var settings = new EngineSettings { Filename = file.Filename, DataStream = data, LogStream = log, DurableCommits = durableCommits };
            (byte[] Data, byte[] Log) image;
            string outcome;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Enumerable.Range(1, 3).Select(id => Row(id, 0)));
                if (durableCommits) log.Pending.Should().Be(0, "the insert was acknowledged: its frames are synced");

                var frames = 0;
                var mark = -1L;
                var written = 0L;
                log.AfterWrite = (position, bytes) =>
                {
                    if (bytes.Length != WalChecksum.FrameSize) return;
                    frames++;
                    if (bytes[BasePage.P_IS_CONFIRMED] == 0) return;
                    log.AfterWrite = null;
                    mark = log.Operations;
                    written = position + bytes.Length;
                    if (sync == "eio") log.FailNextSync = true;
                    if (sync == "cannot-sync") log.CannotSync = true;
                    throw new IOException("injected: the confirmation reached the file, then its write failed");
                };
                Action update = () => rows.Update(Row(2, 7));
                outcome = (string)update.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"];
                log.AfterWrite = null;
                mark.Should().BeGreaterThan(0, "the update's confirmation was written");
                frames.Should().Be(1, "the confirmation is the update's only frame: no earlier frame of it was held");
                log.Live.Length.Should().BeLessThan((int)written, "the failed append was truncated away in the operating system's cache");

                // What a power loss now may leave: the writes up to the confirmation written back, not
                // the truncation after it, unless a sync since made it durable.
                image = (data.Image(ImageKind.WrittenBack), log.Image(ImageKind.WrittenBack, until: mark));
                log.CannotSync = false;

                if (sync == "syncs")
                {
                    outcome.Should().Be("NotCommitted", "the truncation of the confirmation was synced");
                    log.Pending.Should().Be(0, "the sync made the truncation durable");
                }
                else outcome.Should().Be("Unknown", "the truncation that removed the confirmation is not durable");

                // Either way the engine continues read-only (decision 6) and shows only acknowledged commits.
                var record = ReadOnlyAfterWriteFailure.AssertReported(db, sync == "syncs" ? "A commit" : "A WAL write", "log",
                    "injected: the confirmation reached the file");
                ReadOnlyAfterWriteFailure.AssertWriteRefused(() => rows.Update(Row(3, 9)), record);
                Values(db).Should().Equal(0, 0, 0);
            }

            var recovered = FilePowerLossModel.Open(image, Values);
            if (outcome == "NotCommitted") recovered.Should().Equal(new[] { 0, 0, 0 }, "a commit reported not committed is not in the log");
            else recovered.Should().Match<int[]>(x => x.SequenceEqual(new[] { 0, 0, 0 }) || x.SequenceEqual(new[] { 0, 7, 0 }),
                "a commit of unknown outcome is recovered whole or not at all");
            if (sync == "cannot-sync") recovered.Should().Equal(new[] { 0, 7, 0 }, "the confirmation was written back, its truncation was not");

            FilePowerLossModel.Open((data.Image(ImageKind.WrittenBack), log.Image(ImageKind.WrittenBack)), Values)
                .Should().Equal(new[] { 0, 0, 0 }, "once the truncation reached the device, the failed commit is gone");
        }

        /// <summary>
        /// The engine's own files: the commit writes several frames, and its confirmation's write (or an
        /// earlier frame's) returns and then fails. "NotCommitted" is reported only after a log sync that
        /// followed the truncation; a failure of an earlier frame needs none, as no confirmation was written.
        /// </summary>
        [Theory]
        [InlineData("wal-confirmation-after-write", false, null)]
        [InlineData("wal-confirmation-after-write", true, null)]
        [InlineData("wal-confirmation-after-write", false, "secret")]
        [InlineData("wal-confirmation-after-write", true, "secret")]
        [InlineData("wal-page-after-write", false, null)]
        [InlineData("wal-page-after-write", true, null)]
        public void Failed_commit_is_reported_not_committed_only_once_no_confirmation_can_be_in_the_log(string phase, bool eio, string password)
        {
            using var file = new TempFile();
            var connection = password == null ? file.Filename : $"Filename={file.Filename};Password={password}";
            var logName = Path.GetFullPath(FileHelper.GetLogFile(file.Filename));
            var failed = false;
            var syncsAfter = 0;
            NativeFileSync.SimulateErrno = path =>
            {
                if (!failed || Path.GetFullPath(path) != logName) return 0;
                syncsAfter++;
                return eio ? EIO : 0;
            };
            try
            {
                using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, Password = password }))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    db.CheckpointSize = 0;
                    var rows = db.GetCollection("rows");
                    rows.Insert(Enumerable.Range(1, 3).Select(id => Row(id, 0)));

                    engine.SimulateCrashPoint = point =>
                    {
                        if (point != phase || failed) return;
                        failed = true;
                        throw new IOException("injected: the frame reached the file, then its write failed");
                    };
                    Action insert = () => rows.Insert(Enumerable.Range(10, 20).Select(id => Row(id, 0)));
                    var outcome = (string)insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"];
                    engine.SimulateCrashPoint = null;
                    failed.Should().BeTrue();
                    var syncs = syncsAfter;

                    if (phase == "wal-page-after-write")
                    {
                        outcome.Should().Be("NotCommitted", "no confirmation of the commit was written");
                        syncs.Should().Be(0, "nothing needs proof");
                    }
                    else if (!eio)
                    {
                        outcome.Should().Be("NotCommitted");
                        syncs.Should().BeGreaterThan(0, "the truncation of the confirmation was synced before the commit was reported not committed");
                    }
                    else outcome.Should().Be("Unknown", "the truncation of the confirmation could not be made durable");
                    failed = false;

                    var unknown = outcome == "Unknown";
                    var record = ReadOnlyAfterWriteFailure.AssertReported(db, unknown ? "A WAL write" : "A commit", "log",
                        "injected: the frame reached the file");
                    ReadOnlyAfterWriteFailure.AssertWriteRefused(() => rows.Insert(Row(4, 0)), record);
                    rows.Count().Should().Be(3);
                }

                using var reopened = new LiteDatabase(connection);
                reopened.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(new[] { 1, 2, 3 }, "the truncation reached the operating system's cache: the failed commit is not there after a restart");
            }
            finally { NativeFileSync.SimulateErrno = null; }
        }

        /// <summary>
        /// A log whose syncs cannot report failure (a file log synced through the runtime's Flush(true),
        /// no C library bound): the truncation's sync proves nothing, as it proves no retirement or slot
        /// reuse (RuntimeSyncDurability_Tests), so the commit whose confirmation was written is "Unknown".
        /// </summary>
        [Fact]
        public void Confirmation_whose_truncation_sync_cannot_report_failure_is_unknown()
        {
            using var file = new TempFile();
            NativeFileSync.SimulateRuntimeSync = true;
            try
            {
                using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                var rows = db.GetCollection("rows");
                rows.Insert(Row(1, 0));
                db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean.Should().BeFalse("the log's syncs cannot report failure");
                var failed = false;
                engine.SimulateCrashPoint = point =>
                {
                    if (point != "wal-confirmation-after-write" || failed) return;
                    failed = true;
                    throw new IOException("injected: the confirmation reached the file, then its write failed");
                };
                Action insert = () => rows.Insert(Row(2, 0));
                insert.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("Unknown",
                    "a sync that cannot report failure does not prove the truncation durable");
                engine.SimulateCrashPoint = null;
            }
            finally { NativeFileSync.SimulateRuntimeSync = false; }
        }

        /// <summary>
        /// A confirmation whose write returned, followed by a step that failed before the frame was
        /// published (the callback that records its position): the frame stays in the log, not truncated.
        /// The outcome is "Unknown"; before, the batch reported "NotCommitted" with the confirmation there.
        /// </summary>
        [Fact]
        public void Confirmation_written_before_a_later_step_failed_is_unknown()
        {
            var settings = new EngineSettings { DataStream = new MemoryStream(), LogStream = new MemoryStream(), CacheSize = PAGE_SIZE * 8L };
            var state = new EngineState(null, settings);
            using var disk = new DiskService(settings, state, new[] { 2 });
            var page = disk.NewPage();
            page.Write((uint)5, BasePage.P_PAGE_ID);
            page.Write(true, BasePage.P_IS_CONFIRMED);
            Action write = () => disk.WriteLogDisk(new[] { page }, (id, position) => throw new IOException("injected: recording the frame's position failed"));
            write.Should().Throw<IOException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("Unknown");
        }

        /// <summary>
        /// A write failure that is no IOException (EPERM and EACCES reach .NET as
        /// UnauthorizedAccessException, a caller stream may throw anything) carries its outcome too: before,
        /// only an IOException was marked, and such a failed commit carried none.
        /// </summary>
        [Theory]
        [InlineData("wal-page-after-write")]
        [InlineData("wal-confirmation-after-write")]
        public void Failed_commit_whose_write_failure_is_no_io_error_carries_its_outcome(string phase)
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Row(1, 0));
            var failed = false;
            engine.SimulateCrashPoint = point =>
            {
                if (point != phase || failed) return;
                failed = true;
                throw new UnauthorizedAccessException("injected EPERM");
            };
            Action insert = () => rows.Insert(Enumerable.Range(10, 20).Select(id => Row(id, 0)));
            insert.Should().Throw<UnauthorizedAccessException>().Which.Data["LiteDB.CommitOutcome"].Should().Be("NotCommitted",
                "no confirmation was written, or its truncation was synced");
            engine.SimulateCrashPoint = null;
            failed.Should().BeTrue();
        }

        private static BsonDocument Row(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 500)
        };

        private static int[] Values(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().OrderBy(x => x["_id"].AsInt32).Select(x => x["value"].AsInt32).ToArray();
    }
}
#endif
