#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A file format promotion journals the data header into the log, writes the new header and
    /// retires the journal after a data sync. It retired the journal also when that sync answered
    /// "cannot sync" (#2242), and a retiring checkpoint that promoted the file went on to write its
    /// backfill to that data file. Like a checkpoint, a promotion now writes only to a data file
    /// that just synced (it is refused unchanged otherwise), and stops the engine with its journal
    /// kept when the data file stops syncing after its header write; the failure is recorded, and the
    /// engine continues read-only (decision 6 of docs/decisions/durability-policy.md). Power-loss
    /// images keep each file as of its last successful sync, take both as written, or tear the data
    /// header.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncedPromotion_Tests
    {
        private const int Rows = 64;

        /// <summary>
        /// A retiring checkpoint (a live reader keeps the WAL) promotes the file to the MVCC format
        /// first; the data file stops syncing right after the promotion wrote its header. The
        /// checkpoint then wrote its whole backfill to that data file. It now stops: no page is
        /// written, the promotion's journal is kept, and every image opens with commit 9, which was
        /// acknowledged durable. The failure is recorded (decision 6): the engine continues
        /// read-only, reads commit 9 and refuses writes without changing a file.
        /// </summary>
        [Fact]
        public void Retiring_checkpoint_stops_when_its_promotion_header_does_not_sync()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            Header(file.Filename)[HeaderPage.P_FILE_VERSION].Should().BeLessThan(HeaderPage.MVCC_FILE_VERSION);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            var pages = 0;
            settings.CheckpointStage = stage => { if (stage == "data-page" && power.DataFails) pages++; };
            var armed = false;
            using var engine = new LiteEngine(settings);
            engine.SimulateCrashPoint = phase => { if (armed && phase == "promotion-after-header-write") power.DataFails = true; };
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            for (var value = 1; value <= 5; value++)
            {
                db.BeginTrans();
                Update(db, value);
                db.GetCollection("extra").Insert(Enumerable.Range(1, 10).Select(id => Extra(value * 100 + id)));
                db.Commit();
            }
            Exception thrown = null;
            using (var reader = engine.Query("rows", new Query()))
            {
                reader.Read().Should().BeTrue();
                OnAnotherThread(() =>
                {
                    for (var value = 6; value <= 9; value++) Update(db, value);
                    Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue("commit 9 is acknowledged durable");
                    armed = true;
                    try { db.Checkpoint(); } catch (Exception ex) { thrown = ex; }
                    armed = false;
                });
            }
            power.DataFails.Should().BeTrue("the checkpoint promoted the file");
            thrown.Should().BeOfType<IOException>().Which.Message.Should().StartWith(
                "The data file stopped syncing to the device during a file format promotion");
            pages.Should().Be(0, "no page is written to a data file whose sync just failed");

            var synced = power.Capture();
            var data = SyncPowerLossModel.ReadShared(file.Filename);
            var log = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename));
            AssertReadOnly(db, thrown, "A checkpoint", "The data file stopped syncing to the device during a file format promotion", 9, 5);
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the read-only engine writes nothing");
            SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
            HasJournal(synced.Log).Should().BeTrue("the promotion's journal synced and is kept");
            data.Skip(PAGE_SIZE).Should().Equal(synced.Data.Skip(PAGE_SIZE), "only the promoted header was written");
            var torn = TearBeforeLastChange(synced.Data, data);
            foreach (var image in new[] { synced, (data, log), (torn, synced.Log), (torn, log) })
            {
                FilePowerLossModel.Open(image, x =>
                {
                    x.GetCollection("extra").FindAll().Select(d => d["_id"].AsInt32).Should().BeEquivalentTo(
                        Enumerable.Range(1, 5).SelectMany(v => Enumerable.Range(v * 100 + 1, 10)));
                    return SyncPowerLossModel.AssertRows(x, Rows, 9);
                });
            }
        }

        /// <summary>
        /// A compact-storage write would promote a legacy-storage file while the data file cannot sync:
        /// the promotion is refused before it writes anything, and the write falls back to BSON (as
        /// whenever compact storage does not pay) instead of failing and closing the engine; its commit
        /// stays durable in the WAL (decision 4 of docs/decisions/durability-policy.md), which is kept.
        /// The engine does not retry the refused promotion until a data sync succeeds: a commit's log
        /// sync no longer retries it, a checkpoint does. Once the data file syncs, the compact write
        /// after that checkpoint promotes.
        /// </summary>
        [Fact]
        public void Compact_write_stays_bson_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            SetupLegacyStorage(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CompactStorage = CompactStorageMode.Auto;
            var data = SyncPowerLossModel.ReadShared(file.Filename);
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                power.DataFails = true;
                db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
                HasJournal(SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename))).Should().BeFalse("the refused promotion wrote no header journal");
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the promotion wrote nothing");
                db.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 4).Select(Compact), o => o.WithStrictOrdering());
                SyncPowerLossModel.AssertRows(db, Rows, 0);
                Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue("the commit is durable in the WAL while only the data file cannot sync");
                Info(db)["walKept"].AsBoolean.Should().BeTrue("the data file cannot sync");
                Info(db)["writeFailure"].IsNull.Should().BeTrue("a refused promotion is not a failure");

                power.DataFails = false;
                db.GetCollection("compact").Insert(Enumerable.Range(5, 4).Select(Compact));
                Header(file.Filename)[HeaderPage.P_FILE_VERSION].Should().BeLessThan(HeaderPage.COMPACT_FILE_VERSION, "no data sync succeeded before this write");
                db.Checkpoint(); // retries the data sync first
                db.GetCollection("compact").Insert(Enumerable.Range(9, 4).Select(Compact));
                Header(file.Filename)[HeaderPage.P_FILE_VERSION].Should().BeGreaterOrEqualTo(HeaderPage.COMPACT_FILE_VERSION);
            }
            FilePowerLossModel.Open(power.Capture(), x =>
            {
                x.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 12).Select(Compact), o => o.WithStrictOrdering());
                return SyncPowerLossModel.AssertRows(x, Rows, 0);
            });
        }

        /// <summary>
        /// A retiring checkpoint whose data file stops syncing between its retirement proof and the
        /// MVCC promotion's own data sync: the promotion is refused before it writes anything, and the
        /// checkpoint keeps the WAL and returns 0 with both files unchanged, as one whose data file
        /// cannot sync at its start; the engine keeps reading every commit, and every commit
        /// acknowledged durable opens from the files.
        /// </summary>
        [Fact]
        public void Retiring_checkpoint_writes_nothing_when_its_promotion_is_refused()
        {
            using var file = new TempFile();
            SetupRows(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var (data, log) = RefusePromotionInCheckpoint(file.Filename, power, engine, db);
            SyncPowerLossModel.AssertRows(db, Rows, 9);
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data);
            SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
            power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, Rows, 9));
        }

        /// <summary>
        /// Implementation note 6 of docs/decisions/durability-policy.md: a data sync that answers
        /// "cannot sync" before a checkpoint or promotion writes is not a failure, and decision 4 keeps
        /// commits durable in the WAL while only the data file cannot sync. The refused promotion above
        /// writes nothing; it used to be recorded as a write failure ("A checkpoint failed ...: Cannot
        /// upgrade this database's file format now ..."), and the engine refused every later write until
        /// reopened. Nothing is recorded, and the next commit is durable in the WAL.
        /// </summary>
        [Fact]
        public void Refused_promotion_of_a_retiring_checkpoint_is_not_a_write_failure()
        {
            using var file = new TempFile();
            SetupRows(file.Filename);
            using var power = new FilePowerLossModel(file.Filename);
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            RefusePromotionInCheckpoint(file.Filename, power, engine, db);
            Info(db)["writeFailure"].IsNull.Should().BeTrue("the refused promotion wrote nothing (note 6)");
            Info(db)["readOnly"].AsBoolean.Should().BeFalse();
            Update(db, 10);
            Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
            power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, Rows, 10));
        }

        private static void SetupRows(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
        }

        /// <summary>
        /// Commits 1 to 9 under a live reader, then a retiring checkpoint whose data file stops syncing
        /// between its retirement proof and the promotion's own data sync: the checkpoint returns 0, and
        /// the refusal wrote nothing. Returns both files as they were before the checkpoint.
        /// </summary>
        private static (byte[] Data, byte[] Log) RefusePromotionInCheckpoint(string filename, FilePowerLossModel power, LiteEngine engine, LiteDatabase db)
        {
            var logName = FileHelper.GetLogFile(filename);
            db.CheckpointSize = 0;
            for (var value = 1; value <= 5; value++) Update(db, value);
            Exception thrown = null;
            var checkpointed = -1;
            byte[] data = null, log = null;
            using (var reader = engine.Query("rows", new Query()))
            {
                reader.Read().Should().BeTrue();
                OnAnotherThread(() =>
                {
                    for (var value = 6; value <= 9; value++) Update(db, value);
                    Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue("commit 9 is acknowledged durable");
                    data = SyncPowerLossModel.ReadShared(filename);
                    log = SyncPowerLossModel.ReadShared(logName);
                    power.DataFailsFromSync = power.DataSyncs + 2; // the proof's data sync, then the promotion's
                    try { checkpointed = engine.Checkpoint(); } catch (Exception ex) { thrown = ex; }
                });
            }
            thrown.Should().BeNull("a checkpoint refused before it wrote keeps the WAL, as one whose data file cannot sync at its start");
            checkpointed.Should().Be(0);
            power.DataSyncs.Should().BeGreaterOrEqualTo(power.DataFailsFromSync, "the promotion tried its data sync");
            SyncPowerLossModel.ReadShared(filename).Should().Equal(data, "the refused promotion wrote nothing");
            SyncPowerLossModel.ReadShared(logName).Should().Equal(log);
            return (data, log);
        }

        /// <summary>
        /// The data file stops syncing right after a compact-storage write's promotion wrote the new
        /// header. The promotion retired its journal anyway, leaving the header change in the OS cache
        /// with no durable recovery copy. It now stops the engine with the journal kept: the write is
        /// not acknowledged, the engine continues read-only (decision 6) with the rows as they were and
        /// refuses writes without changing a file, and every image, also one with the header torn,
        /// opens with the rows as they were.
        /// </summary>
        [Fact]
        public void Promotion_whose_header_does_not_sync_stops_with_its_journal()
        {
            using var file = new TempFile();
            SetupLegacyStorage(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CompactStorage = CompactStorageMode.Auto;
            using var engine = new LiteEngine(settings);
            engine.SimulateCrashPoint = phase => { if (phase == "promotion-after-header-write") power.DataFails = true; };
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            Action insert = () => db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
            var thrown = insert.Should().Throw<IOException>().WithMessage(
                "The data file stopped syncing to the device during a file format promotion*").Which;

            var synced = power.Capture();
            var data = SyncPowerLossModel.ReadShared(file.Filename);
            var log = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename));
            // walKept: see Promotion_failure_record_reports_its_kept_header_journal.
            AssertReadOnly(db, thrown, "A file format promotion", "The data file stopped syncing to the device during a file format promotion", 0, 0, walKept: null);
            db.GetCollectionNames().Should().NotContain("compact");
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data, "the read-only engine writes nothing");
            SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename)).Should().Equal(log);
            HasJournal(synced.Log).Should().BeTrue("the journal synced before the header was written, and is kept");
            HasJournal(log).Should().BeTrue();
            var torn = TearBeforeLastChange(synced.Data, data);
            foreach (var image in new[] { synced, (data, log), (torn, synced.Log), (torn, log) })
            {
                FilePowerLossModel.Open(image, x =>
                {
                    x.GetCollectionNames().Should().NotContain("compact");
                    return SyncPowerLossModel.AssertRows(x, Rows, 0);
                });
            }
        }

        /// <summary>
        /// Suspected engine defect, left failing: the promotion above stops with its header journal kept
        /// in the log file (the torn header's only durable recovery copy; decision 1 of
        /// docs/decisions/durability-policy.md keeps the WAL and its header journal alike), but its
        /// recorded failure says walKept false and "The log file was empty.", which invites deleting that
        /// log: DiskService.RecordWriteFailure counts only the WAL's frames (GetFileLength excludes the
        /// journal). Expected: walKept true and "The log file was kept."
        /// </summary>
        [Fact]
        public void Promotion_failure_record_reports_its_kept_header_journal()
        {
            using var file = new TempFile();
            SetupLegacyStorage(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CompactStorage = CompactStorageMode.Auto;
            using var engine = new LiteEngine(settings);
            engine.SimulateCrashPoint = phase => { if (phase == "promotion-after-header-write") power.DataFails = true; };
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            Action insert = () => db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
            insert.Should().Throw<IOException>().WithMessage("The data file stopped syncing to the device during a file format promotion*");
            HasJournal(SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename))).Should().BeTrue("the log file keeps the header journal");

            var info = Info(db);
            info["writeFailure"]["walKept"].AsBoolean.Should().BeTrue("the log file holds the kept header journal");
            info["readOnlyReason"].AsString.Should().EndWith("The log file was kept.");
        }

        /// <summary>
        /// A file whose checksums an earlier engine enabled but whose indexes still need the migration
        /// (v10): the migration's promotion needs a data sync, so while the data file cannot sync the
        /// writable migration is refused up front, and the open falls back to the read-only remedy
        /// the refusal names: it reads every row, refuses writes, and leaves both files unchanged;
        /// an explicit read-only open works too, and once the data file syncs the file migrates.
        /// </summary>
        [Fact]
        public void Index_migration_of_a_checksummed_file_opens_read_only_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            SetupIndexMigration(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var data = File.ReadAllBytes(file.Filename);
            using (var power = new FilePowerLossModel(file.Filename))
            {
                power.DataFails = true;
                using (var db = LiteDB.Tests.Engine.IndexMigration_Tests.Open(file.Filename, null))
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused, x => SyncPowerLossModel.AssertRows(x, Rows, 0))
                        .Should().Contain("readonly=true;legacy index scan=true");
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                (File.Exists(logName) ? File.ReadAllBytes(logName).Length : 0).Should().Be(0);
                using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true, LegacyIndexScan = true })))
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                power.DataFails = false;
                using (var db = LiteDB.Tests.Engine.IndexMigration_Tests.Open(file.Filename, null))
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
            }
            LiteDB.Tests.Engine.IndexMigration_Tests.ReadHeader(file.Filename, null)[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);
        }

        /// <summary>
        /// The same migration where the data file syncs for the migration's check at open, then not
        /// for the promotion's own sync (refused before it writes anything: "Cannot upgrade this
        /// database's file format now"), or only up to the promotion's header write (it stops with
        /// its header journal kept: "stopped syncing"). Either failed the open; it now opens
        /// read-only with that refusal as its reason, reads every row, refuses writes, and writes
        /// nothing itself. The files as last synced are the original ones and open with every row;
        /// once the data file syncs, the file migrates.
        /// </summary>
        [Theory]
        [InlineData(2, "Cannot upgrade this database's file format now")]
        [InlineData(3, "The data file stopped syncing to the device during a file format promotion")]
        public void Index_migration_whose_promotion_cannot_sync_opens_read_only(int failsFromSync, string cause)
        {
            using var file = new TempFile();
            SetupIndexMigration(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var original = File.ReadAllBytes(file.Filename);
            try
            {
                using var power = new FilePowerLossModel(file.Filename) { DataFailsFromSync = failsFromSync };
                byte[] data, log;
                using (var db = LiteDB.Tests.Engine.IndexMigration_Tests.Open(file.Filename, null))
                {
                    power.DataSyncs.Should().Be(failsFromSync, "the migration's check synced, the promotion's last sync did not");
                    (data, log) = (File.ReadAllBytes(file.Filename), File.Exists(logName) ? File.ReadAllBytes(logName) : null);
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, cause, x => SyncPowerLossModel.AssertRows(x, Rows, 0));
                }
                File.ReadAllBytes(file.Filename).Should().Equal(data, "the read-only engine wrote nothing");
                if (failsFromSync == 2)
                {
                    data.Should().Equal(original, "the refused promotion wrote nothing");
                    File.Exists(logName).Should().BeFalse();
                }
                else
                {
                    File.ReadAllBytes(logName).Should().Equal(log);
                    HasJournal(log).Should().BeTrue("the stopped promotion keeps its header journal");
                }
                power.Capture().Data.Should().Equal(original, "no promoted header synced");
                power.AfterPowerLoss(x => SyncPowerLossModel.AssertRows(x, Rows, 0));

                power.DataFailsFromSync = 0;
                using (var db = LiteDB.Tests.Engine.IndexMigration_Tests.Open(file.Filename, null))
                {
                    Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                    SyncPowerLossModel.AssertRows(db, Rows, 0);
                }
            }
            finally { File.Delete(logName); }
            LiteDB.Tests.Engine.IndexMigration_Tests.ReadHeader(file.Filename, null)[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.INDEX_FILE_VERSION);
        }

        /// <summary>A file whose checksums an earlier engine enabled, whose indexes still need the migration (v10).</summary>
        private static void SetupIndexMigration(string filename)
        {
            using (var db = LiteDB.Tests.Engine.IndexMigration_Tests.Open(filename, null))
                db.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            LiteDB.Tests.Engine.IndexMigration_Tests.RewriteHeaders(filename, null, header =>
            {
                header[HeaderPage.P_FILE_VERSION] = HeaderPage.CHECKSUM_FILE_VERSION;
                header[EnginePragmas.P_INDEX_ORDER_VERSION] = 0;
                Array.Clear(header, EnginePragmas.P_COLLATION_STAMP, 4);
            });
        }

        private static void SetupLegacyStorage(string filename)
        {
            using var setup = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = filename, CompactStorage = CompactStorageMode.Legacy }));
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
            setup.Checkpoint();
            Header(filename)[HeaderPage.P_FILE_VERSION].Should().BeLessThan(HeaderPage.COMPACT_FILE_VERSION);
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        /// <summary>A document compact storage pays for (repeated long field names), so writing it promotes the file.</summary>
        private static BsonDocument Compact(int id) => new BsonDocument
        {
            ["_id"] = id, ["longRepeatedFieldName"] = id, ["anotherLongRepeatedFieldName"] = "payload",
            ["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = id, ["anotherNestedFieldName"] = true },
            ["arrayValues"] = new BsonArray(Enumerable.Range(1, 30).Select(x => new BsonValue(x)))
        };

        private static BsonDocument Extra(int id) => new BsonDocument { ["_id"] = id, ["p"] = new string('e', 3000) };

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        /// <summary>
        /// After a recorded failure (<paramref name="thrown"/>) of <paramref name="operation"/> on the
        /// data file (decision 6) the engine continues read-only: it reads commit <paramref name="value"/>
        /// and the extra rows of commits 1 to <paramref name="extras"/>, reports the failure (with the
        /// log file kept, <paramref name="walKept"/>; null: not checked), and refuses a write with it.
        /// </summary>
        private static void AssertReadOnly(LiteDatabase db, Exception thrown, string operation, string error, int value, int extras, bool? walKept = true)
        {
            SyncPowerLossModel.AssertRows(db, Rows, value);
            db.GetCollection("extra").FindAll().Select(d => d["_id"].AsInt32).Should().BeEquivalentTo(
                Enumerable.Range(1, extras).SelectMany(v => Enumerable.Range(v * 100 + 1, 10)));
            var record = ReadOnlyAfterWriteFailure.AssertReported(db, operation, "data", error, walKept);
            ReadOnlyAfterWriteFailure.AssertWriteRefused(() => Update(db, value + 1), record)
                .InnerException.Should().BeSameAs(thrown, "the refusal carries the recorded failure");
            SyncPowerLossModel.AssertRows(db, Rows, value);
        }

        private static byte[] Header(string filename) => SyncPowerLossModel.ReadShared(filename).Take(PAGE_SIZE).ToArray();

        private static bool HasJournal(byte[] log)
        {
            using var stream = new MemoryStream(log);
            return HeaderJournal.Read(stream) != null;
        }

        /// <summary>
        /// The data file as last synced with the new header written back up to its last changed byte:
        /// neither the old nor the new header, so only the kept journal repairs it.
        /// </summary>
        private static byte[] TearBeforeLastChange(byte[] synced, byte[] current)
        {
            var last = Enumerable.Range(0, PAGE_SIZE).Last(i => synced[i] != current[i]);
            var torn = (byte[])synced.Clone();
            Buffer.BlockCopy(current, 0, torn, 0, last);
            Action unrepaired = () => FilePowerLossModel.Open((torn, new byte[0]), x => x.GetCollectionNames().ToArray());
            unrepaired.Should().Throw<LiteException>().WithMessage("Checksum mismatch in Data file at position 0*");
            return torn;
        }

        /// <summary>The reader's transaction belongs to this thread: the checkpoint runs on another.</summary>
        private static void OnAnotherThread(Action action)
        {
            ExceptionDispatchInfo failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            });
            thread.Start();
            thread.Join();
            failure?.Throw();
        }
    }
}
#endif
