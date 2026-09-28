#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// An open that must first convert, migrate or repair the file needs a data sync before it
    /// removes a log or its header's recovery copy (#2242). Where the data file cannot sync, that
    /// writable open was refused, and the application could not even read its data without
    /// knowing to reopen with "readonly=true". The engine now opens read-only on its own instead:
    /// $database reports readOnly and readOnlyReason (the refusal), reads and explicit
    /// transactions that only read work, and every write throws an IOException naming the refusal
    /// before it changes anything, after which the same instance keeps reading. The read-only open
    /// writes nothing; once the data file syncs, a writable open converts or repairs the file.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class UnsyncedReadOnlyOpen_Tests
    {
        /// <summary>Refusals a writable open falls back from (their messages start so).</summary>
        internal const string ConversionRefused = "Cannot convert this legacy database now";
        internal const string RecoveryRefused = "Cannot recover this database now";
        internal const string StoppedSyncing = "The data file stopped syncing";

        /// <summary>A write's error on an engine that opened read-only on its own; the refusal follows.</summary>
        internal const string WriteRefused = "Cannot modify this database: it opened read-only because the writable open was refused. ";

        private const int PlainRows = 48;

        /// <summary>
        /// A 5.0.21 file (IndexMigration_5_0_21.zip, its WAL empty) on storage whose data file cannot
        /// sync, opened the plain way. Its conversion needs a data sync, so the open threw "Cannot
        /// convert this legacy database now" and nothing could be read. It now opens read-only:
        /// every row reads, a query on an indexed field finds the right rows (it scans documents,
        /// as the legacy index keeps the old order), an explicit transaction reads and rolls back,
        /// and a write throws before any change, also inside that transaction. The file is
        /// unchanged, no log is created and nothing is synced. Once the data file syncs, a plain
        /// open converts, and a commit it reports durable survives a power loss.
        /// </summary>
        [Fact]
        public void Legacy_file_opens_read_only_while_its_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                using (var db = new LiteDatabase(file.Filename))
                {
                    var syncs = power.DataSyncs;
                    var reason = AssertReadOnlyFallback(db, ConversionRefused, x => AssertPlainRows(x));
                    reason.Should().Contain("cannot sync");
                    PlanIndex(db).Should().Be("_id", "the legacy index keeps the old order: the query scans documents");

                    db.BeginTrans().Should().BeTrue("an explicit transaction that only reads is accepted");
                    AssertPlainRows(db);
                    AssertWriteRefused(db, reason);
                    AssertPlainRows(db);
                    db.Rollback().Should().BeTrue();
                    AssertPlainRows(db);
                    power.DataSyncs.Should().Be(syncs, "the read-only engine syncs nothing");
                }
                File.ReadAllBytes(file.Filename).Should().Equal(original, "the read-only open wrote nothing");
                File.Exists(logName).Should().BeFalse("nor created a log");

                power.DataFails = false;
                using (var db = new LiteDatabase(file.Filename))
                {
                    var info = Info(db);
                    info["readOnly"].AsBoolean.Should().BeFalse();
                    info["readOnlyReason"].IsNull.Should().BeTrue();
                    info["checksums"].AsBoolean.Should().BeTrue("the writable open converted the file");
                    PlanIndex(db).Should().Be("oid");
                    db.GetCollection("rows").Insert(PlainRow(PlainRows + 1));
                    Info(db)["durableLogFlush"].AsBoolean.Should().BeTrue();
                }
                power.AfterPowerLoss(db => AssertPlainRows(db, PlainRows + 1));
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// The same file through a shared connection, whose every operation opens an engine of its
        /// own: each falls back to read-only, so reads work and a write throws the same catchable
        /// IOException, also inside an explicit transaction, after which the connection keeps
        /// reading; nothing is written. Once the data file syncs, the connection's next write
        /// converts the file.
        /// </summary>
        [Fact]
        public void Shared_connection_to_a_legacy_file_reads_while_its_data_file_cannot_sync()
        {
            using var file = new TempFile();
            var original = Fixture("plain.db");
            File.WriteAllBytes(file.Filename, original);
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                using (var db = new LiteDatabase($"Filename={file.Filename};Connection=shared"))
                {
                    var reason = AssertReadOnlyFallback(db, ConversionRefused, x => AssertPlainRows(x));
                    db.BeginTrans().Should().BeTrue();
                    AssertPlainRows(db);
                    AssertWriteRefused(db, reason);
                    db.Rollback().Should().BeTrue();
                    AssertPlainRows(db);
                    File.ReadAllBytes(file.Filename).Should().Equal(original, "the read-only opens wrote nothing");
                    File.Exists(logName).Should().BeFalse();

                    power.DataFails = false;
                    db.GetCollection("rows").Insert(PlainRow(PlainRows + 1));
                    AssertPlainRows(db, PlainRows + 1);
                    Info(db)["readOnlyReason"].IsNull.Should().BeTrue("the write's writable open converted the file");
                }
                using var converted = new LiteDatabase(file.Filename);
                Info(converted)["checksums"].AsBoolean.Should().BeTrue();
                AssertPlainRows(converted, PlainRows + 1);
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// Control: a writable open that succeeds reports no reason, and an explicit read-only
        /// open reports readOnly without one; its write and transaction errors stay as they were.
        /// </summary>
        [Fact]
        public void Healthy_and_explicitly_read_only_opens_report_no_read_only_reason()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection("rows").Insert(PlainRow(1));
                var info = Info(db);
                info["readOnly"].AsBoolean.Should().BeFalse();
                info["readOnlyReason"].IsNull.Should().BeTrue();
            }
            var data = File.ReadAllBytes(file.Filename);
            using (var db = new LiteDatabase($"Filename={file.Filename};ReadOnly=true"))
            {
                var info = Info(db);
                info["readOnly"].AsBoolean.Should().BeTrue();
                info["readOnlyReason"].IsNull.Should().BeTrue();
                Action write = () => db.GetCollection("rows").Insert(PlainRow(2));
                write.Should().Throw<IOException>().Which.Message.Should().Be("Cannot modify a read-only database.");
                Action begin = () => db.BeginTrans();
                begin.Should().Throw<IOException>().Which.Message.Should().Be("Cannot start a transaction in a read-only database.");
                db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).Should().Equal(1);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(data);
        }

        /// <summary>
        /// The engine falls back on its own copy of the settings: the caller's EngineSettings stay
        /// writable (were they changed, every later open with them would be read-only), so the
        /// same settings open writable once the data file syncs.
        /// </summary>
        [Fact]
        public void Fallback_leaves_the_callers_settings_unchanged()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, Fixture("plain.db"));
            var logName = FileHelper.GetLogFile(file.Filename);
            using var power = new FilePowerLossModel(file.Filename) { DataFails = true };
            try
            {
                var settings = new EngineSettings { Filename = file.Filename };
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                    AssertReadOnlyFallback(db, ConversionRefused, x => AssertPlainRows(x));
                settings.ReadOnly.Should().BeFalse();
                settings.LegacyIndexScan.Should().BeFalse();
                settings.ReadOnlyCause.Should().BeNull();

                power.DataFails = false;
                using (var engine = new LiteEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    Info(db)["readOnly"].AsBoolean.Should().BeFalse();
                    db.GetCollection("rows").Insert(PlainRow(PlainRows + 1));
                    AssertPlainRows(db, PlainRows + 1);
                }
            }
            finally { File.Delete(logName); }
        }

        /// <summary>
        /// <paramref name="db"/> opened read-only because its writable open was refused for a data
        /// file that cannot sync: $database reports readOnly and the refusal (starting with
        /// <paramref name="cause"/>), <paramref name="read"/> finds the exact state, a write throws
        /// before any change naming the refusal, and the same instance then reads that state
        /// again. Returns the refusal.
        /// </summary>
        internal static string AssertReadOnlyFallback(LiteDatabase db, string cause, Action<LiteDatabase> read, string collection = "rows")
        {
            var reason = ReadOnlyReason(db);
            reason.Should().StartWith(cause);
            read(db);
            AssertWriteRefused(db, reason, collection);
            read(db);
            ReadOnlyReason(db).Should().Be(reason);
            return reason;
        }

        /// <summary>$database of an engine that opened read-only on its own: the refusal it fell back from.</summary>
        internal static string ReadOnlyReason(LiteDatabase db)
        {
            var info = Info(db);
            info["readOnly"].AsBoolean.Should().BeTrue("the writable open was refused");
            info["readOnlyReason"].IsString.Should().BeTrue("$database says why the open is read-only");
            return info["readOnlyReason"].AsString;
        }

        /// <summary>A write to <paramref name="collection"/> throws the read-only error that names <paramref name="reason"/>.</summary>
        internal static void AssertWriteRefused(LiteDatabase db, string reason, string collection = "rows")
        {
            Action write = () => db.GetCollection(collection).Insert(new BsonDocument { ["_id"] = 900001 });
            write.Should().Throw<IOException>().Which.Message.Should().Be(WriteRefused + reason);
        }

        /// <summary>
        /// <paramref name="write"/> throws the read-only error of an engine that opened read-only
        /// because its writable open was refused with a message starting with <paramref name="cause"/>.
        /// </summary>
        internal static void AssertWriteRefused(Action write, string cause) =>
            write.Should().Throw<IOException>().Which.Message.Should().StartWith(WriteRefused + cause);

        /// <summary>
        /// The rows of IndexMigration_5_0_21.zip's files (see IndexMigrationPowerLoss_Tests), and
        /// those <see cref="PlainRow"/> added up to <paramref name="count"/>: each byte for byte, and
        /// found by queries on the indexed oid and values fields.
        /// </summary>
        internal static int AssertPlainRows(LiteDatabase db, int count = PlainRows)
        {
            var rows = db.GetCollection("rows");
            var expected = Enumerable.Range(1, count).Select(PlainRow).ToArray();
            var actual = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            actual.Select(x => x["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            for (var i = 0; i < count; i++)
                BsonSerializer.Serialize(actual[i]).Should().Equal(BsonSerializer.Serialize(expected[i]));
            foreach (var row in expected)
                rows.Find(Query.EQ("oid", row["oid"])).Select(x => x["_id"].AsInt32).Should().Equal(row["_id"].AsInt32);
            rows.Find(BsonExpression.Create("$.values ANY = @0", 0.1m)).Select(x => x["_id"].AsInt32).OrderBy(x => x)
                .Should().Equal(Enumerable.Range(1, count));
            return count;
        }

        /// <summary>Row <paramref name="id"/> of IndexMigration_5_0_21.zip's files (ids 1..48), or one shaped like them.</summary>
        internal static BsonDocument PlainRow(int id) => new BsonDocument
        {
            ["_id"] = id,
            ["oid"] = new ObjectId((id % 2 == 0 ? "80000000" : "7fffffff") + "1122334455" + id.ToString("x6")),
            ["values"] = new BsonArray { 0.1m, 0.1d },
            ["number"] = 0.1m,
            ["payload"] = new string((char)('a' + id % 26), 240) + id
        };

        private static string PlanIndex(LiteDatabase db) => db.GetCollection("rows").Query()
            .Where(BsonExpression.Create("$.oid = @0", PlainRow(3)["oid"])).GetPlan()["index"]["name"].AsString;

        private static BsonDocument Info(LiteDatabase db) => db.GetCollection("$database").FindAll().Single();

        internal static byte[] Fixture(string name)
        {
            using var resource = typeof(UnsyncedReadOnlyOpen_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
