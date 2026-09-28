#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Since data barriers degrade on storage that answers "cannot sync" (#2242), a full checkpoint
    /// can write its backfill to a data file that never syncs it. If the WAL still syncs, emptying
    /// it becomes durable at the next log sync, before the backfill ever does: a power loss (each
    /// file as of its last successful sync) then loses every commit the WAL held, also commits
    /// acknowledged while durableLogFlush was true, and an independent connection's later commit,
    /// which it reports durable. Such a checkpoint keeps the WAL instead.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncedBackfillPowerLoss_Tests
    {
        [Theory]
        [InlineData("shared")]
        [InlineData("second")] // the commit after the checkpoint comes from an independent connection
        [InlineData("direct")] // one long-lived engine, which also finds out when the data file syncs again
        public void Full_checkpoint_whose_data_sync_fails_keeps_the_wal(string mode)
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);

            using ILiteEngine first = mode == "direct" ? new LiteEngine(power.Settings()) : new SharedEngine(power.Settings());
            using var firstDb = new LiteDatabase(first, disposeOnClose: false);
            firstDb.CheckpointSize = 0;
            for (var value = 1; value <= 9; value++)
            {
                Update(firstDb, value);
                DurableLogFlush(firstDb).Should().BeTrue();
            }
            power.DataFails = true;
            firstDb.Checkpoint();
            DurableLogFlush(firstDb).Should().BeFalse("the data file cannot sync");
            new FileInfo(logName).Length.Should().BeGreaterThan(0, "the WAL keeps what the data file could not make durable");

            using var second = mode == "second" ? new SharedEngine(power.Settings()) : null;
            using var writer = second == null ? firstDb : new LiteDatabase(second, disposeOnClose: false);
            Update(writer, 10);

            power.AfterPowerLoss(Rows).Should().Be(10, "the synced WAL holds every commit");
            power.DataFails = false;
            writer.Checkpoint();
            new FileInfo(logName).Length.Should().Be(0, "once the data file syncs again, a full checkpoint empties the WAL");
            power.AfterPowerLoss(Rows).Should().Be(10);
        }

        /// <summary>
        /// A kept WAL is rewritten by every full checkpoint. Automatic checkpoints wait until it has
        /// doubled instead of running on every commit once it exceeds the checkpoint size.
        /// </summary>
        [Fact]
        public void Automatic_checkpoints_are_rationed_while_the_wal_is_kept()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename) { DataFails = true };
            var checkpoints = 0;
            var settings = power.Settings();
            settings.CheckpointStage = stage => { if (stage == "before-commit-lock") checkpoints++; };
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                db.CheckpointSize = 10;
                for (var id = 1; id <= 300; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                db.GetCollection("log").Count().Should().Be(300);
            }
            checkpoints.Should().BeLessThan(20, "a kept WAL of n pages costs O(log n) automatic checkpoints, not one per commit");
            power.AfterPowerLoss(Rows).Should().Be(0);
        }

        /// <summary>
        /// A connection's rationing of a kept WAL ends when another connection empties the WAL:
        /// its automatic checkpoints do not wait until a new WAL doubles the old kept length.
        /// </summary>
        [Fact]
        public void Kept_wal_rationing_ends_when_another_connection_empties_the_wal()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            try
            {
                using var engine = new SharedEngine(power.Settings());
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                for (var id = 1; id <= 200; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                power.DataFails = true;
                db.Checkpoint();
                new FileInfo(logName).Length.Should().BeGreaterThan(0, "the full checkpoint kept the WAL");
                power.DataFails = false;

                using (var other = new SharedEngine(power.Settings()))
                using (var otherDb = new LiteDatabase(other, disposeOnClose: false))
                    otherDb.Checkpoint();
                new FileInfo(logName).Length.Should().Be(0);

                db.CheckpointSize = 10;
                for (var id = 201; id <= 300; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
                new FileInfo(logName).Length.Should().BeLessThan(80L * Constants.PAGE_SIZE, "automatic checkpoints run again");
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A WAL the engine keeps in memory (LiteDatabase(Stream) without a log stream) protects
        /// nothing, so a data stream that cannot sync does not keep it: it stays bounded.
        /// </summary>
        [Fact]
        public void In_memory_wal_is_still_emptied_on_a_data_stream_that_cannot_sync()
        {
            using var data = new UnsyncableStream();
            using var db = new LiteDatabase(data);
            db.CheckpointSize = 10;
            for (var id = 1; id <= 200; id++) db.GetCollection("log").Insert(new BsonDocument { ["_id"] = id, ["text"] = new string('t', 3000) });
            data.Rejected.Should().BeGreaterThan(0);
            db.GetCollection("$database").FindAll().Single()["logFileSize"].AsInt64.Should().BeLessThan(40 * Constants.PAGE_SIZE);
            db.GetCollection("log").Count().Should().Be(200);
        }

        /// <summary>Control: storage where neither file syncs still empties the WAL, as before.</summary>
        [Fact]
        public void Full_checkpoint_empties_the_wal_when_neither_file_syncs()
        {
            using var file = new TempFile();
            Setup(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);

            using (var db = new LiteDatabase(new LiteEngine(power.Settings())))
            {
                db.CheckpointSize = 0;
                for (var value = 1; value <= 3; value++) Update(db, value);
                power.DataFails = power.LogFails = true;
                db.Checkpoint();
                new FileInfo(logName).Length.Should().Be(0);
            }
            using var reopened = new LiteDatabase(new LiteEngine(power.Settings()));
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Should().OnlyContain(x => x == 3);
        }

        /// <summary>
        /// A 5.0.21 file with WAL commits (WalCrash_5_0_21.zip, see LegacyWalSharedMigration_Tests)
        /// on storage whose data file cannot sync while its WAL can: conversion needs an empty WAL,
        /// so it is refused with a diagnostic naming the storage. The legacy WAL is kept byte for
        /// byte and the data file stays unconverted (its backfill is what 5.0.21 would write), so
        /// the database opens with every commit once the storage syncs.
        /// </summary>
        [Fact]
        public void Legacy_conversion_is_refused_and_keeps_the_wal_when_only_the_wal_syncs()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(file.Filename, Entry("crash.db"));
            File.WriteAllBytes(logName, Entry("crash-log.db"));
            var version = File.ReadAllBytes(file.Filename)[HeaderPage.P_FILE_VERSION];
            var log = File.ReadAllBytes(logName);
            try
            {
                using (var power = new SyncPowerLossModel(file.Filename) { DataFails = true })
                {
                    Action open = () => new LiteEngine(power.Settings()).Dispose();
                    open.Should().Throw<IOException>().WithMessage("Cannot convert this legacy database*data file cannot be synced*readonly=true;legacy index scan=true*");
                    SyncPowerLossModel.ReadShared(logName).Should().Equal(log);
                    SyncPowerLossModel.ReadShared(file.Filename)[HeaderPage.P_FILE_VERSION].Should().Be(version);
                }

                // The diagnostic's way to read it meanwhile.
                using (var readOnly = new LiteDatabase($"Filename={file.Filename};readonly=true;legacy index scan=true"))
                {
                    readOnly.GetCollection("docs").Count().Should().Be(101);
                }
                SyncPowerLossModel.ReadShared(logName).Should().Equal(log);

                using var db = new LiteDatabase(file.Filename);
                var docs = db.GetCollection("docs").FindAll().ToList();
                docs.Should().HaveCount(101);
                docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
            }
            finally { File.Delete(logName); }
        }

        private sealed class UnsyncableStream : MemoryStream, IDurableStream
        {
            internal int Rejected;

            public void FlushToDisk()
            {
                Rejected++;
                throw new UnauthorizedAccessException("sync unsupported");
            }
        }

        private const int Rows = 64;

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static byte[] Entry(string name)
        {
            using var resource = typeof(UnsyncedBackfillPowerLoss_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.WalCrash_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
