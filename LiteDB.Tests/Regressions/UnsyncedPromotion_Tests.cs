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
    /// kept when the data file stops syncing after its header write. Power-loss images keep each
    /// file as of its last successful sync, take both as written, or tear the data header.
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
        /// acknowledged durable.
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
            Action query = () => db.GetCollection("rows").Count();
            query.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure*");

            var synced = power.Capture();
            var data = SyncPowerLossModel.ReadShared(file.Filename);
            var log = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename));
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
        /// A compact-storage write promotes a legacy-storage file while the data file cannot sync:
        /// the promotion is refused before it writes anything. Like every I/O failure in a transaction
        /// the refusal closes the engine (as in 5.0.21), but both files are byte-identical, so the
        /// database reopens as it was; once the data file syncs, the same write promotes it.
        /// </summary>
        [Fact]
        public void Promotion_is_refused_unchanged_while_the_data_file_cannot_sync()
        {
            using var file = new TempFile();
            SetupLegacyStorage(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.CompactStorage = CompactStorageMode.Auto;
            var data = SyncPowerLossModel.ReadShared(file.Filename);
            var log = SyncPowerLossModel.ReadShared(logName);
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                power.DataFails = true;
                Action refused = () => db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
                refused.Should().Throw<IOException>().WithMessage("Cannot upgrade this database's file format now*");
            }
            SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(data);
            SyncPowerLossModel.ReadShared(logName).Should().Equal(log);
            using (var db = new LiteDatabase(new LiteEngine(settings)))
            {
                SyncPowerLossModel.AssertRows(db, Rows, 0);
                db.GetCollectionNames().Should().NotContain("compact");
                power.DataFails = false;
                db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
                db.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 4).Select(Compact), o => o.WithStrictOrdering());
            }
            Header(file.Filename)[HeaderPage.P_FILE_VERSION].Should().BeGreaterOrEqualTo(HeaderPage.COMPACT_FILE_VERSION);
            FilePowerLossModel.Open(power.Capture(), x =>
            {
                x.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 4).Select(Compact), o => o.WithStrictOrdering());
                return SyncPowerLossModel.AssertRows(x, Rows, 0);
            });
        }

        /// <summary>
        /// The data file stops syncing right after a compact-storage write's promotion wrote the new
        /// header. The promotion retired its journal anyway, leaving the header change in the OS cache
        /// with no durable recovery copy. It now stops the engine with the journal kept: the write is
        /// not acknowledged, and every image, also one with the header torn, opens with the rows as
        /// they were.
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
            insert.Should().Throw<IOException>().WithMessage(
                "The data file stopped syncing to the device during a file format promotion*");
            Action query = () => db.GetCollection("rows").Count();
            query.Should().Throw<IOException>().WithMessage("Engine closed after an I/O failure*");

            var synced = power.Capture();
            var data = SyncPowerLossModel.ReadShared(file.Filename);
            var log = SyncPowerLossModel.ReadShared(FileHelper.GetLogFile(file.Filename));
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
