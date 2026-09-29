using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// #2242: some network shares and virtual file systems reject FlushFileBuffers/fsync. Opened
    /// with "durable commits=false" (the caller accepts commits a power loss may lose), such a log
    /// keeps taking commits: they stay ordered in the OS cache, which survives a process crash, no
    /// power-loss guarantee is claimed, and "cannot sync" is not a failure (proposed default A of
    /// docs/decisions/durability-policy.md). What it no longer backs is an in-place overwrite of the
    /// data file (a checkpoint's backfill, a conversion, a format promotion): its recovery copy, the
    /// header journal and WAL, would be in the OS cache only, and a power loss mid-overwrite would
    /// tear the data file (external review, point 1; decision D protects the data file's integrity
    /// for callers that opted out too). A checkpoint writes nothing and keeps the WAL; an open that
    /// must convert or promote the file opens read-only, and a write that must promote it is
    /// refused, both files byte for byte. With durable commits (the default) no commit is
    /// acknowledged there (decision 3): a commit throws before it writes a frame, and an open that
    /// must convert or promote the file first opens read-only instead. Either way every row reads.
    /// </summary>
    public class Issue2242_UnsyncableLog_Tests
    {
        [Fact]
        public void New_database_on_a_log_that_never_syncs_writes_checkpoints_and_reopens_without_durable_commits()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();

            using (var db = Open(data, log, durableCommits: false))
            {
                var rows = db.GetCollection("rows");
                rows.Insert(Documents(0, 16));
                rows.EnsureIndex("value");
                db.Checkpoint();
                rows.Insert(Documents(16, 16));
                db.Checkpoint();
                rows.Update(Documents(0, 32, value: 1));

                DurableLogFlush(db).Should().BeFalse("the weaker guarantee must be discoverable");
                log.Rejections.Should().BeGreaterThan(0, "each checkpoint still tries a real sync");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
            }

            AssertDocuments(data, log, count: 32, value: 1);
        }

        /// <summary>
        /// Decision 3: the proof before the engine's first commit finds that the log cannot sync, so
        /// that commit throws before it writes a frame. The data file stays byte for byte, the log
        /// stays empty, reads work, $database reports the failure, and the next write throws with it
        /// without asking the log again (decision 6).
        /// </summary>
        [Fact]
        public void New_database_on_a_log_that_never_syncs_refuses_a_durable_commit_before_it_writes()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();

            using var db = Open(data, log);
            var rows = db.GetCollection("rows");
            var created = data.ToArray();

            Action insert = () => rows.Insert(Documents(0, 16));
            insert.Should().Throw<IOException>().WithMessage(WriteFailureAssert.NotWritten + "the log file cannot sync*")
                .Which.InnerException.Should().BeOfType<UnauthorizedAccessException>();
            log.Rejections.Should().Be(1, "the proof asked once");
            log.Length.Should().Be(0, "no frame was written");
            data.ToArray().Should().Equal(created);

            rows.Count().Should().Be(0);
            var reason = WriteFailureAssert.Recorded(db, "A commit", "log", WriteFailureAssert.NotWritten + "the log file cannot sync", walKept: false);
            WriteFailureAssert.Refused(() => rows.Insert(Documents(0, 1)), reason);
            log.Rejections.Should().Be(1, "a refused write asks the storage nothing");
            log.Length.Should().Be(0);
            data.ToArray().Should().Equal(created);
        }

        /// <summary>
        /// The conversion of a legacy file overwrites its header in place behind a header journal. Opted
        /// out of durable commits, it used to proceed in write order behind a journal in the OS cache
        /// only, which a power loss mid-conversion could leave torn (external review, point 1); decision
        /// D keeps the recovery rule for callers that opted out, so a log that cannot sync refuses it
        /// before the journal, as with durable commits. The refusal is tagged as unsynced storage, so the
        /// open falls back to read-only (decision 2): every row reads, through the index too, $database
        /// says why and records no failure (proposed default A), a write throws naming the refusal, and
        /// neither file changes. Once the log syncs, the next open converts the file and writes.
        /// </summary>
        [Theory]
        [InlineData(8)]
        [InlineData(9)]
        public void Legacy_database_on_a_log_that_never_syncs_opens_read_only_without_durable_commits(byte version)
        {
            using var data = Legacy(version, out var log);
            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();

            using (var db = Open(data, log, durableCommits: false))
            {
                var info = WriteFailureAssert.Info(db);
                info["checksumCoverage"].AsString.Should().Be("Legacy", "the conversion was refused before it wrote");
                info["readOnly"].AsBoolean.Should().BeTrue();
                info["durableLogFlush"].AsBoolean.Should().BeFalse();
                var reason = info["readOnlyReason"].AsString;
                reason.Should().StartWith(WriteFailureAssert.LogCannotSync + "a conversion writes nothing");
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
                AssertRows(db, count: WalTestDatabase.DocumentCount, value: 0);

                Action update = () => db.GetCollection("rows").UpdateMany("{ value: 1 }", "true");
                update.Should().Throw<IOException>().Which.Message.Should().Be(WriteFailureAssert.OpenRefused + reason);
                AssertRows(db, count: WalTestDatabase.DocumentCount, value: 0);
            }
            data.ToArray().Should().Equal(dataBefore);
            log.ToArray().Should().Equal(logBefore);
            log.Rejections.Should().BeGreaterThan(0);

            log.Syncs = true;
            using (var db = Open(data, log, durableCommits: false))
            {
                var info = WriteFailureAssert.Info(db);
                info["readOnly"].AsBoolean.Should().BeFalse();
                info["checksumCoverage"].AsString.Should().Be("Mixed", "the conversion goes through once the log syncs");
                db.GetCollection("rows").UpdateMany("{ value: 1 }", "true");
                db.Checkpoint();
                log.Length.Should().Be(0, "a checkpoint drains the WAL once the log syncs");
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 100, ["value"] = 1 });
                WriteFailureAssert.NoneRecorded(db);
            }

            AssertDocuments(data, log, count: WalTestDatabase.DocumentCount + 1, value: 1);
            log.Dispose();
        }

        /// <summary>
        /// The conversion of a legacy file syncs its log as a recovery barrier. With durable commits a
        /// log that cannot sync refuses it before it writes (decision 3), so the open falls back to
        /// read-only (decision 2): every row reads, $database says why, a write throws naming the
        /// refusal, and neither file changes.
        /// </summary>
        [Theory]
        [InlineData(8)]
        [InlineData(9)]
        public void Legacy_database_on_a_log_that_never_syncs_opens_read_only_with_durable_commits(byte version)
        {
            using var data = Legacy(version, out var log);
            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();

            using (var db = Open(data, log))
            {
                var info = WriteFailureAssert.Info(db);
                info["checksumCoverage"].AsString.Should().Be("Legacy", "the conversion was refused before it wrote");
                info["readOnly"].AsBoolean.Should().BeTrue();
                var reason = info["readOnlyReason"].AsString;
                reason.Should().StartWith(WriteFailureAssert.LogCannotSync);
                db.GetCollection("rows").FindAll().Should().BeEquivalentTo(Documents(0, WalTestDatabase.DocumentCount));

                Action update = () => db.GetCollection("rows").UpdateMany("{ value: 1 }", "true");
                update.Should().Throw<IOException>().Which.Message.Should().Be(WriteFailureAssert.OpenRefused + reason);
                db.GetCollection("rows").FindAll().Should().BeEquivalentTo(Documents(0, WalTestDatabase.DocumentCount));
            }

            data.ToArray().Should().Equal(dataBefore);
            log.ToArray().Should().Equal(logBefore);
            log.Dispose();
        }

        /// <summary>
        /// The first compact write promotes v11 to v12, overwriting the header in place behind a header
        /// journal. Opted out of durable commits, it used to publish v12 in write order behind a journal
        /// in the OS cache only, which a power loss mid-promotion could leave torn (external review,
        /// point 1); decision D keeps the recovery rule for callers that opted out, so a log that cannot
        /// sync refuses the promotion before its journal. The write falls back to BSON, as while the data
        /// file cannot sync (Snapshot.TryRequireCompactVersion): it succeeds, the data file stays byte
        /// for byte and v11, and no failure is recorded ("cannot sync" is the reason to opt out, proposed
        /// default A). Once the log syncs, a checkpoint drains the WAL and the next compact write promotes.
        /// </summary>
        [Fact]
        public void Compact_promotion_on_a_log_that_never_syncs_falls_back_to_bson_without_durable_commits()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();
            V11(data, log);

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Compact, DurableCommits = false }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                var dataBefore = data.ToArray();
                var syncsBefore = log.Rejections;
                var rows = db.GetCollection("rows");
                rows.Insert(CompactDocuments());
                rows.Insert(CompactDocuments(32));
                log.Rejections.Should().Be(syncsBefore + 1, "the promotion tried the log once; later compact writes skip it while it cannot sync");
                data.ToArray().Should().Equal(dataBefore, "the promotion was refused before it wrote, and no checkpoint ran");

                rows.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(0, 48));
                rows.Find(Query.GTE("_id", 16)).Should().BeEquivalentTo(CompactDocuments().Concat(CompactDocuments(32)));
                rows.Find(Query.EQ("value", 1)).Should().HaveCount(32);
                WriteFailureAssert.NoneRecorded(db, "\"cannot sync\" is the reason to opt out, not a failure");
                DurableLogFlush(db).Should().BeFalse();
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION, "the file stays v11: BSON only");

            log.Syncs = true;
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Compact, DurableCommits = false }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.Checkpoint();
                log.Length.Should().Be(0, "once the log syncs, a checkpoint drains the WAL");
                var rows = db.GetCollection("rows");
                rows.Update(CompactDocuments());
                data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.COMPACT_FILE_VERSION, "and the promotion goes through");
                rows.Update(Documents(0, 16, value: 1));
                WriteFailureAssert.NoneRecorded(db);
            }
            AssertDocuments(data, log, count: 48, value: 1);
        }

        /// <summary>
        /// The first compact write promotes v11 to v12 behind a log sync. With durable commits a log
        /// that cannot sync refuses that write before the data file changes (decision 3): the file
        /// stays v11 byte for byte. Reads must keep working, $database must report the failure, and
        /// the next write must throw with it (decisions 2 and 6).
        /// Fails today (engine defect): the promotion's first log sync (BeginHeaderJournal, before
        /// DiskService.WriteFileVersion counts journal bytes) throws inside the insert, where
        /// ExecuteAutoTransaction stops the engine through EngineState.Handle without recording a write
        /// failure, so the engine closes for good and the read throws "Engine closed after an I/O
        /// failure" instead of reopening read-only.
        /// </summary>
        [Fact]
        public void Compact_promotion_on_a_log_that_never_syncs_is_refused_and_keeps_reading_with_durable_commits()
        {
            using var data = new MemoryStream();
            using var log = new UnsyncableLog();
            V11(data, log);
            var dataBefore = data.ToArray();
            var logBefore = log.ToArray();

            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Compact }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                var rows = db.GetCollection("rows");
                Action insert = () => rows.Insert(CompactDocuments());
                // The promotion's barrier or the commit's proof may refuse it: either names the log.
                insert.Should().Throw<IOException>().WithMessage("*the log file cannot sync to the device (#2242)*");
                data.ToArray().Should().Equal(dataBefore);
                log.ToArray().Should().Equal(logBefore);

                rows.FindAll().Should().BeEquivalentTo(Documents(0, 16), "reading is possible, so it is allowed");
                var info = WriteFailureAssert.Info(db);
                info["readOnly"].AsBoolean.Should().BeTrue();
                info["writeFailure"]["file"].AsString.Should().Be("log");
                info["writeFailure"]["error"].AsString.Should().ContainEquivalentOf("the log file cannot sync to the device (#2242)");
                WriteFailureAssert.Refused(() => rows.Update(Documents(0, 16, value: 1)), info["readOnlyReason"].AsString);
                rows.FindAll().Should().BeEquivalentTo(Documents(0, 16));
            }

            data.ToArray().Should().Equal(dataBefore, "the file stays v11");
            log.ToArray().Should().Equal(logBefore);
        }

        // A v11 file with rows 0..15 on a log that never syncs (opted out to write them). The checkpoint
        // writes nothing there (decision D), so the rows stay in the WAL.
        private static void V11(MemoryStream data, UnsyncableLog log)
        {
            using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, CompactStorage = CompactStorageMode.Legacy, DurableCommits = false }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("rows").Insert(Documents(0, 16));
                db.Checkpoint();
            }
            data.ToArray()[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);
        }

        // Repeated field names make the compact representation beneficial.
        private static BsonDocument[] CompactDocuments(int first = 16) => Enumerable.Range(first, 16).Select(id =>
        {
            var document = LiteDB.Tests.Engine.CompactStorage_Tests.Document(id);
            document["value"] = 1;
            return document;
        }).ToArray();

        // A legacy (v8 or v9) copy of WalTestDatabase's seeded rows, on a log that never syncs.
        private static MemoryStream Legacy(byte version, out UnsyncableLog log)
        {
            using var source = new WalTestDatabase(password: null);
            source.Seed("rows");
            source.Database.GetCollection("rows").EnsureIndex("value");
            source.Database.Checkpoint();
            var data = ChecksumTestFiles.Copy(source.Data.ToArray());
            log = new UnsyncableLog();
            ChecksumTestFiles.MakeLegacy(data, log, password: null, version);
            return data;
        }

        // Reopen a copy of what a killed process leaves behind (every byte handed to the OS), on the
        // same storage, so without durable commits.
        private static void AssertDocuments(MemoryStream data, MemoryStream log, int count, int value)
        {
            using var dataCopy = ChecksumTestFiles.Copy(data.ToArray());
            using var logCopy = new UnsyncableLog();
            var logBytes = log.ToArray();
            logCopy.Write(logBytes, 0, logBytes.Length);
            logCopy.Position = 0;

            using var db = Open(dataCopy, logCopy, durableCommits: false);
            var rows = db.GetCollection("rows");
            rows.Count().Should().Be(count);
            rows.Find(Query.EQ("value", value)).Should().HaveCount(count);
            rows.Insert(new BsonDocument { ["_id"] = -1, ["value"] = value });
            db.Checkpoint();
            rows.Count().Should().Be(count + 1);
        }

        /// <summary>"rows" holds exactly <see cref="Documents"/> 0..<paramref name="count"/>-1 of <paramref name="value"/>, and a query on value finds each and no other.</summary>
        private static void AssertRows(LiteDatabase db, int count, int value)
        {
            var rows = db.GetCollection("rows");
            rows.FindAll().Should().BeEquivalentTo(Documents(0, count, value));
            rows.Find(Query.EQ("value", value)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(0, count));
            rows.Find(Query.Not("value", value)).Should().BeEmpty();
        }

        private static LiteDatabase Open(Stream data, Stream log, bool durableCommits = true)
        {
            var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log, DurableCommits = durableCommits });
            var db = new LiteDatabase(engine);
            // A pragma is a write: with durable commits on this log it would be the refused commit.
            if (!durableCommits) db.CheckpointSize = 0;
            return db;
        }

        private static BsonDocument[] Documents(int first, int count, int value = 0) =>
            Enumerable.Range(first, count).Select(id => new BsonDocument
            {
                ["_id"] = id, ["value"] = value, ["payload"] = new string('x', 1500)
            }).ToArray();

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        /// <summary>A log whose storage answers every device sync with ERROR_ACCESS_DENIED, until <see cref="Syncs"/> is set.</summary>
        private sealed class UnsyncableLog : MemoryStream, IDurableStream
        {
            internal int Rejections;
            internal bool Syncs;

            public void FlushToDisk()
            {
                if (Syncs) return;
                Rejections++;
                throw new UnauthorizedAccessException("Access to the path is denied.");
            }
        }
    }
}
