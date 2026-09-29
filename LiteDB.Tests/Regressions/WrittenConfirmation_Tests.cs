#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

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

        private static BsonDocument Row(int id, int value) => new BsonDocument
        {
            ["_id"] = id, ["value"] = value, ["payload"] = new string('p', 500)
        };

        private static int[] Values(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().OrderBy(x => x["_id"].AsInt32).Select(x => x["value"].AsInt32).ToArray();
    }
}
#endif
