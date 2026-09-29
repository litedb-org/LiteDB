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
