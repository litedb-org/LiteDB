#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// A file format promotion refused before it wrote while an earlier header journal is still
    /// outstanding is no failure (DiskService.WriteFileVersion, implementation note 6). That state is
    /// not reachable in an engine that has not failed: no promotion starts with an earlier journal
    /// outstanding. The only open that leaves a journal outstanding for a writable engine recovers a
    /// legacy conversion journal whose converted header never reached the device (RecoverHeaderJournal
    /// keeps its legacy redo). Its legacy header (below v11) needs the index migration, whose drain
    /// backfills that redo behind the kept journal and then empties the log, so the conversion journals
    /// its own header and the migration's promotion finds no journal. The conversion whose own header
    /// does not sync refuses the open before that promotion, and an open retires every other journal it
    /// recovers before it accepts a write. <see cref="EngineState.ObservePromotion"/> reports each
    /// promotion as it starts under the WAL writer. Plain and encrypted files.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class KeptJournalPromotion_Tests
    {
        private const string Password = "migration-power-loss";
        private const string PromotionRefused = "Cannot upgrade this database's file format now";

        /// <summary>
        /// A 5.0.21 file whose data file syncs for the conversion's first syncs, not for its converted
        /// header: the conversion keeps the legacy header's backup and its journal, and the open falls
        /// back to read-only before the migration's promotion starts. The files a power loss leaves are
        /// the legacy data file beside that kept journal.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(Password)]
        public void Conversion_whose_header_does_not_sync_refuses_the_open_before_any_promotion(string password)
        {
            using var file = new TempFile();
            List<(byte Version, bool JournalOutstanding)> promotions = null;
            var image = KeepConversionJournal(file.Filename, password, x => promotions = x);
            promotions.Should().BeEmpty("the conversion kept its journal and refused the open before the migration's promotion");
            image.Data.Should().Equal(Fixture(password), "the converted header never synced");
            AssertConversionJournal(image.Log, password);
        }

        /// <summary>
        /// The next open of those files recovers the kept legacy journal and keeps it outstanding (the
        /// legacy redo stays until a checkpoint backfilled it), the precondition of a promotion refused
        /// behind an earlier journal. While the data file cannot sync, the open is refused before its
        /// migration and no promotion starts: a plain file opens read-only after writing the journal's
        /// copy of the legacy header back, an encrypted one fails as its data writer syncs its preamble;
        /// the log and the files a power loss leaves are unchanged. Once the data file syncs, the
        /// migration's drain backfills behind the kept journal (it writes none of its own) and retires it
        /// with the legacy WAL; the conversion journals its own header and empties the log; the promotion
        /// then starts with no journal outstanding. Refused there (the data file stops syncing as it
        /// starts), it writes nothing and records no failure, the open falls back to read-only, and the
        /// files a power loss leaves open with every row. With syncing storage the open migrates, every
        /// later promotion also starts with no journal outstanding, and the files open with every row.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(Password)]
        public void Open_that_keeps_a_legacy_conversion_journal_retires_it_before_the_migrations_promotion(string password)
        {
            (byte[] Data, byte[] Log) kept;
            using (var source = new TempFile())
                kept = KeepConversionJournal(source.Filename, password, _ => { });
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                File.WriteAllBytes(file.Filename, kept.Data);
                File.WriteAllBytes(logName, kept.Log);
                using var power = new FilePowerLossModel(file.Filename);

                // The data file cannot sync: the open recovers the kept journal and is refused before its migration.
                power.DataFails = true;
                var promotions = ObservePromotions(() =>
                {
                    if (password != null)
                    {
                        // An encrypted data writer makes its preamble durable as it is created: the writable open fails there.
                        Action open = () => Open(file.Filename, password, null).Dispose();
                        open.Should().Throw<IOException>().WithMessage("Device sync of*failed (errno 22)*");
                        return;
                    }
                    using var db = Open(file.Filename, password, null);
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused,
                        x => UnsyncedReadOnlyOpen_Tests.AssertPlainRows(x));
                });
                promotions.Should().BeEmpty("the open was refused before its migration");
                var refused = SyncPowerLossModel.ReadShared(file.Filename);
                PlainHeader(refused, password).Should().Equal(password == null ? Journal(kept.Log, password).Header : PlainHeader(kept.Data, password),
                    "the recovery writes the journal's copy of the legacy header back (its transaction ID aside, the same header)");
                OutsideHeader(refused, password).Should().Equal(OutsideHeader(kept.Data, password));
                SyncPowerLossModel.ReadShared(logName).Should().Equal(kept.Log, "the journal is kept");
                power.Capture().Data.Should().Equal(kept.Data);
                power.Capture().Log.Should().Equal(kept.Log);

                // The data file syncs until the migration's promotion starts.
                power.DataFails = false;
                byte[] beforeReclaim = null, afterReclaim = null;
                var journaledAgain = false;
                (byte[] Data, byte[] Log) atPromotion = (null, null);
                BeforeDataSyncs(file.Filename, () =>
                {
                    if (afterReclaim != null && !journaledAgain && TryJournal(ReadLog(logName), password)?.ConfirmsLegacyBackup == true) journaledAgain = true;
                });
                promotions = ObservePromotions(() =>
                {
                    using var db = Open(file.Filename, password, stage =>
                    {
                        if (stage == "before-reclaim") beforeReclaim = ReadLog(logName);
                        if (stage == "after-reclaim") afterReclaim = ReadLog(logName);
                    });
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, PromotionRefused, x => UnsyncedReadOnlyOpen_Tests.AssertPlainRows(x));
                    Info(db)["writeFailure"].IsNull.Should().BeTrue("a promotion refused before it wrote is no failure");
                }, version =>
                {
                    atPromotion = (SyncPowerLossModel.ReadShared(file.Filename), ReadLog(logName));
                    power.DataFails = true;
                });
                promotions.Should().Equal(new[] { (HeaderPage.INDEX_FILE_VERSION, false) }, "the drain retired the kept journal before the conversion");
                beforeReclaim.Should().Equal(kept.Log, "the drain backfilled the legacy redo behind the kept journal, writing no journal of its own");
                LogContentLength(afterReclaim, password).Should().Be(0, "the drain retired the kept journal with the legacy WAL");
                journaledAgain.Should().BeTrue("the conversion journaled its own header before syncing it");
                LogContentLength(atPromotion.Log, password).Should().Be(0, "the conversion retired its own journal once its header synced");
                PlainHeader(atPromotion.Data, password)[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.CHECKSUM_FILE_VERSION);
                SyncPowerLossModel.ReadShared(file.Filename).Should().Equal(atPromotion.Data, "the refused promotion wrote nothing");
                LogContentLength(ReadLog(logName), password).Should().Be(0, "the log still holds nothing (the failed engine's close deletes an empty one)");
                power.Capture().Data.Should().Equal(atPromotion.Data, "the converted header synced before the promotion");
                FilePowerLossModel.Open(power.Capture(), x => UnsyncedReadOnlyOpen_Tests.AssertPlainRows(x), password);

                // The storage syncs: the open migrates, and later promotions find no journal either.
                power.DataFails = false;
                promotions = ObservePromotions(() =>
                {
                    using var db = Open(file.Filename, password, null);
                    Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                    UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db);
                    db.GetCollection("rows").Insert(UnsyncedReadOnlyOpen_Tests.PlainRow(49));
                    db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
                    db.Checkpoint();
                    UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db, 49);
                });
                promotions.Select(x => x.Version).Should().StartWith(new[] { HeaderPage.INDEX_FILE_VERSION, HeaderPage.COMPACT_FILE_VERSION });
                promotions.Should().OnlyContain(x => !x.JournalOutstanding);
                FilePowerLossModel.Open(power.Capture(), AssertMigrated, password);
                using (var reopened = Open(file.Filename, password, null)) AssertMigrated(reopened);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The other journals an open recovers: a compact write's promotion whose header write was torn
        /// after its journal synced (the data file stopped syncing there) leaves a checksummed file
        /// whose header only its journal repairs. The open restores the header and retires the journal
        /// before it accepts a write: the next promotion starts with no journal outstanding.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData(Password)]
        public void Open_that_repairs_a_header_from_a_promotion_journal_retires_it_before_the_next_promotion(string password)
        {
            (byte[] Data, byte[] Log) torn;
            using (var source = new TempFile())
                torn = TearPromotionHeader(source.Filename, password);
            var promotions = ObservePromotions(() => FilePowerLossModel.Open(torn, db =>
            {
                SyncPowerLossModel.AssertRows(db, Rows, 0);
                db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
                db.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 4).Select(Compact), o => o.WithStrictOrdering());
                return SyncPowerLossModel.AssertRows(db, Rows, 0);
            }, password));
            promotions.Should().Equal(new[] { (HeaderPage.COMPACT_FILE_VERSION, false) }, "the open retired the journal it repaired the header from");
        }

        private const int Rows = 16;

        /// <summary>
        /// Open the 5.0.21 fixture writable while the data file stops syncing from the conversion's
        /// converted header on: the open falls back to read-only (the conversion keeps its journal),
        /// with the promotions it started (<paramref name="observed"/>). Returns the files a power loss
        /// leaves: the legacy data file and the log with the legacy header's backup and its journal.
        /// </summary>
        private static (byte[] Data, byte[] Log) KeepConversionJournal(string filename, string password,
            Action<List<(byte Version, bool JournalOutstanding)>> observed)
        {
            var original = Fixture(password);
            File.WriteAllBytes(filename, original);
            var logName = FileHelper.GetLogFile(filename);
            try
            {
                using var power = new FilePowerLossModel(filename);
                // The data file stops syncing once the conversion journal is in the log: at the sync of the converted header.
                BeforeDataSyncs(filename, () =>
                {
                    if (!power.DataFails && TryJournal(ReadLog(logName), password)?.ConfirmsLegacyBackup == true) power.DataFails = true;
                });
                byte[] data = null, log = null;
                observed(ObservePromotions(() =>
                {
                    using var db = Open(filename, password, null);
                    power.DataFails.Should().BeTrue("the conversion reached the sync of its converted header");
                    (data, log) = (SyncPowerLossModel.ReadShared(filename), ReadLog(logName));
                    UnsyncedReadOnlyOpen_Tests.AssertReadOnlyFallback(db, UnsyncedReadOnlyOpen_Tests.ConversionRefused,
                        x => UnsyncedReadOnlyOpen_Tests.AssertPlainRows(x));
                }));
                AssertConversionJournal(log, password);
                PlainHeader(data, password)[HeaderPage.P_FILE_VERSION].Should().Be(HeaderPage.CHECKSUM_FILE_VERSION,
                    "the converted header was written, its sync failed");
                SyncPowerLossModel.ReadShared(filename).Should().Equal(data, "the read-only engine wrote nothing");
                ReadLog(logName).Should().Equal(log);
                return power.Capture();
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// A checksummed file of <see cref="Rows"/> rows in legacy storage (v11) whose first compact write's
        /// promotion synced its journal and then wrote the new header while the data file stopped syncing
        /// (the engine stopped with the journal kept): the log as written, and the data file as last
        /// synced with the new header written back only up to its last changed byte (neither header).
        /// </summary>
        private static (byte[] Data, byte[] Log) TearPromotionHeader(string filename, string password)
        {
            using (var setup = new LiteDatabase(new LiteEngine(new EngineSettings { Filename = filename, Password = password, CompactStorage = CompactStorageMode.Legacy })))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
                setup.Checkpoint();
            }
            var logName = FileHelper.GetLogFile(filename);
            using var power = new SyncPowerLossModel(filename);
            var settings = power.Settings();
            settings.Password = password;
            byte[] data, log;
            using (var engine = new LiteEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.SimulateCrashPoint = phase => { if (phase == "promotion-after-header-write") power.DataFails = true; };
                Action insert = () => db.GetCollection("compact").Insert(Enumerable.Range(1, 4).Select(Compact));
                insert.Should().Throw<IOException>().WithMessage("The data file stopped syncing to the device during a file format promotion*");
                data = SyncPowerLossModel.ReadShared(filename);
                log = SyncPowerLossModel.ReadShared(logName);
            }
            var synced = power.Capture().Data;
            Journal(log, password).Should().NotBeNull("the promotion kept its journal");
            var last = Enumerable.Range(0, Math.Min(synced.Length, data.Length)).Last(i => synced[i] != data[i]);
            var torn = (byte[])synced.Clone();
            Buffer.BlockCopy(data, 0, torn, 0, last);
            Action unrepaired = () => FilePowerLossModel.Open((torn, new byte[0]), x => x.GetCollectionNames().ToArray(), password);
            unrepaired.Should().Throw<LiteException>().WithMessage("Checksum mismatch in Data file at position 0*");
            return (torn, log);
        }

        /// <summary>
        /// Promotions started on this thread while <paramref name="action"/> runs: the version each
        /// promotes to and whether an earlier header journal was outstanding; <paramref name="atStart"/>
        /// runs as each starts, before it syncs or writes.
        /// </summary>
        private static List<(byte Version, bool JournalOutstanding)> ObservePromotions(Action action, Action<byte> atStart = null)
        {
            var thread = Thread.CurrentThread.ManagedThreadId;
            var promotions = new List<(byte Version, bool JournalOutstanding)>();
            EngineState.ObservePromotion = (version, outstanding) =>
            {
                if (Thread.CurrentThread.ManagedThreadId != thread) return;
                promotions.Add((version, outstanding));
                atStart?.Invoke(version);
            };
            try { action(); }
            finally { EngineState.ObservePromotion = null; }
            return promotions;
        }

        private static LiteDatabase Open(string filename, string password, Action<string> checkpointStage) =>
            new LiteDatabase(new LiteEngine(new EngineSettings { Filename = filename, Password = password, CheckpointStage = checkpointStage }));

        /// <summary>The rows the migrated database holds: the fixture's, row 49 and four compact rows.</summary>
        private static int AssertMigrated(LiteDatabase db)
        {
            Info(db)["checksums"].AsBoolean.Should().BeTrue();
            db.GetCollection("compact").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 4).Select(Compact), o => o.WithStrictOrdering());
            return UnsyncedReadOnlyOpen_Tests.AssertPlainRows(db, 49);
        }

        /// <summary><paramref name="log"/> holds the legacy conversion's journal: the legacy header, its backup confirmed.</summary>
        private static void AssertConversionJournal(byte[] log, string password)
        {
            var journal = Journal(log, password);
            journal.Should().NotBeNull("the conversion keeps its journal");
            journal.Legacy.Should().BeTrue();
            journal.ConfirmsLegacyBackup.Should().BeTrue();
        }

        /// <summary>The header journal at the end of the log file's bytes (decrypted with <paramref name="password"/>), or null.</summary>
        private static HeaderJournal Journal(byte[] log, string password)
        {
            if (log.Length < PAGE_SIZE) return null;
            using var copy = ChecksumTestFiles.Copy(log);
            using var factory = new StreamFactory(copy, password);
            using var stream = factory.GetStream(false, false);
            return HeaderJournal.Read(stream);
        }

        /// <summary><see cref="Journal"/> of a log the engine may be writing: null where it cannot be read yet.</summary>
        private static HeaderJournal TryJournal(byte[] log, string password)
        {
            try { return Journal(log, password); }
            catch (Exception ex) when (ex is IOException || ex is LiteException) { return null; }
        }

        /// <summary>The length of the log file's content (decrypted with <paramref name="password"/>): 0 when it holds nothing.</summary>
        private static long LogContentLength(byte[] log, string password)
        {
            if (log.Length == 0) return 0;
            using var copy = ChecksumTestFiles.Copy(log);
            using var factory = new StreamFactory(copy, password);
            using var stream = factory.GetStream(false, false);
            return stream.Length;
        }

        /// <summary>
        /// Run <paramref name="atSync"/> at each sync of the data file <paramref name="filename"/>, before the
        /// power-loss model installed first answers it; disposing the model removes both.
        /// </summary>
        private static void BeforeDataSyncs(string filename, Action atSync)
        {
            var model = NativeFileSync.SimulateErrno;
            var data = Path.GetFullPath(filename);
            NativeFileSync.SimulateErrno = path =>
            {
                if (string.Equals(Path.GetFullPath(path), data, StringComparison.OrdinalIgnoreCase)) atSync();
                return model(path);
            };
        }

        /// <summary>The data file's header page, decrypted with <paramref name="password"/>.</summary>
        private static byte[] PlainHeader(byte[] data, string password)
        {
            using var copy = ChecksumTestFiles.Copy(data);
            using var factory = new StreamFactory(copy, password);
            using var stream = factory.GetStream(false, false);
            var header = new byte[PAGE_SIZE];
            stream.ReadRequired(header, 0, header.Length);
            return header;
        }

        /// <summary>The data file's bytes other than its header page (an encrypted file's follows its preamble page).</summary>
        private static byte[] OutsideHeader(byte[] data, string password)
        {
            var header = password == null ? 0 : PAGE_SIZE;
            return data.Take(header).Concat(data.Skip(header + PAGE_SIZE)).ToArray();
        }

        private static byte[] ReadLog(string logName) => File.Exists(logName) ? SyncPowerLossModel.ReadShared(logName) : new byte[0];

        private static byte[] Fixture(string password) => UnsyncedReadOnlyOpen_Tests.Fixture(password == null ? "plain.db" : "encrypted.db");

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        /// <summary>A document compact storage pays for (repeated long field names), so writing it promotes the file.</summary>
        private static BsonDocument Compact(int id) => new BsonDocument
        {
            ["_id"] = id, ["longRepeatedFieldName"] = id, ["anotherLongRepeatedFieldName"] = "payload",
            ["nestedDocument"] = new BsonDocument { ["longNestedFieldName"] = id, ["anotherNestedFieldName"] = true },
            ["arrayValues"] = new BsonArray(Enumerable.Range(1, 30).Select(x => new BsonValue(x)))
        };
    }
}
#endif
