using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    public class IndexMigrationPowerLoss_Tests
    {
        private readonly ITestOutputHelper _output;
        public IndexMigrationPowerLoss_Tests(ITestOutputHelper output) { _output = output; }

        [Fact]
        public void Encrypted_migration_recovers_an_interrupted_new_wal_preamble()
        {
            const string password = "migration-power-loss";
            var original = Fixture(password, out _);
            using var device = new IndexMigrationCrashDevice(original, Array.Empty<byte>());
            Migrate(device, password);
            // Preserve the exact failure image even if the repaired writer now
            // groups its marker/salt write and no longer emits that boundary.
            var markerOnly = new IndexMigrationCrashDevice.Image { Data = original, Log = new byte[] { 1 } };
            var shortPreambles = device.Images.Where(x => x.Log.Length > 0 && x.Log.Length < Constants.PAGE_SIZE).ToArray();
            shortPreambles.Should().NotBeEmpty();
            try
            {
                AssertRecovered(markerOnly, password);
                foreach (var image in shortPreambles) AssertRecovered(image, password);
            }
            catch
            {
                var directory = Path.Combine(Path.GetTempPath(), "litedb-index-migration-preamble");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "migration.db"), markerOnly.Data);
                File.WriteAllBytes(Path.Combine(directory, "migration-log.db"), markerOnly.Log);
                _output.WriteLine("Failing physical images: " + directory);
                throw;
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("migration-power-loss")]
        public void Released_indexes_survive_power_loss_and_another_interrupted_recovery(string password)
        {
            var original = Fixture(password, out var originalLog);
            using var device = new IndexMigrationCrashDevice(original, originalLog);
            Migrate(device, password);
            _output.WriteLine("Initial physical crash boundaries: " + device.PhysicalBoundaries + "; distinct images: " + device.Images.Count);
            device.Events.Should().Contain("migration:data.torn-write");
            device.Events.Should().Contain("migration:log.torn-write");
            device.Events.Should().Contain("checkpoint:data.torn-write");
            device.Events.Should().Contain("checkpoint:log.after-truncate");

            var verified = new HashSet<string>();
            foreach (var image in device.Images.GroupBy(Signature).Select(x => x.First()))
            {
                using var recovery = new IndexMigrationCrashDevice(image.Data, image.Log);
                recovery.FirstEventOnly = true;
                Migrate(recovery, password);
                // Repeat each kind of repair/publication/checkpoint fault. Equal
                // physical images need only one recovery; every first-crash
                // boundary is captured before this deduplication.
                foreach (var repeated in recovery.Images)
                    if (verified.Add(Signature(repeated)))
                    {
                        try { AssertRecovered(repeated, password); }
                        catch (Exception ex)
                        {
                            _output.WriteLine("First crash: " + image.Event + "; second crash: " + repeated.Event);
                            _output.WriteLine("Second image sizes: " + repeated.Data.Length + "/" + repeated.Log.Length);
                            throw new InvalidOperationException("Recovery after " + image.Event + " / " + repeated.Event, ex);
                        }
                    }
            }
            _output.WriteLine("Distinct second-crash recovery images: " + verified.Count);
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, device.Data.ToArray());
            File.WriteAllBytes(FileHelper.GetLogFile(file.Filename), device.Log.ToArray());
            using var reopened = new LiteDatabase(new ConnectionString
                { Filename = file.Filename, Password = password, ReadOnly = true });
            Verify(reopened);
        }

        private static string Signature(IndexMigrationCrashDevice.Image image)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(image.Data)) + ":" + Convert.ToBase64String(sha.ComputeHash(image.Log));
        }

        private static byte[] Fixture(string password, out byte[] log)
        {
            using var resource = typeof(IndexMigrationPowerLoss_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.IndexMigration_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using (var logEntry = zip.GetEntry(password == null ? "plain-log.db" : "encrypted-log.db").Open())
            using (var logBytes = new MemoryStream())
            {
                logEntry.CopyTo(logBytes);
                log = logBytes.ToArray();
            }
            using var entry = zip.GetEntry(password == null ? "plain.db" : "encrypted.db").Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            using var physical = ChecksumTestFiles.Copy(bytes.ToArray());
            using var factory = new StreamFactory(physical, password);
            using var plain = factory.GetStream(false, false);
            var header = new byte[Constants.PAGE_SIZE];
            plain.ReadRequired(header, 0, header.Length);
            header[HeaderPage.P_FILE_VERSION].Should().Be(8);
            header[EnginePragmas.P_INDEX_ORDER_VERSION].Should().Be(0);
            BitConverter.ToUInt32(header, EnginePragmas.P_COLLATION_STAMP).Should().Be(0);
            return bytes.ToArray();
        }

        private static EngineSettings Settings(Stream data, Stream log, string password, bool readOnly = false) =>
            new EngineSettings { DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly,
                TransactionPageLimit = 4, DurableCommits = true };

        private static void Migrate(IndexMigrationCrashDevice device, string password)
        {
            using var db = new LiteDatabase(new LiteEngine(Settings(device.Data, device.Log, password)));
            Verify(db);
            device.Stage = "checkpoint";
            db.Checkpoint();
            device.Armed = false;
        }

        private static void AssertRecovered(IndexMigrationCrashDevice.Image image, string password)
        {
            using var data = ChecksumTestFiles.Copy(image.Data);
            using var log = ChecksumTestFiles.Copy(image.Log);
            using (var db = new LiteDatabase(new LiteEngine(Settings(data, log, password))))
            {
                Verify(db);
                db.Checkpoint();
            }
            var completedData = data.ToArray();
            var completedLog = log.ToArray();
            using (var db = new LiteDatabase(new LiteEngine(Settings(data, log, password, true)))) Verify(db);
            data.ToArray().Should().Equal(completedData);
            log.ToArray().Should().Equal(completedLog);
        }

        private static void Verify(LiteDatabase db)
        {
            db.LimitSize.Should().Be(1024 * 1024);
            var rows = db.GetCollection("rows");
            var expected = Enumerable.Range(1, 48).Select(i => new BsonDocument
            {
                ["_id"] = i,
                ["oid"] = new ObjectId((i % 2 == 0 ? "80000000" : "7fffffff") + "1122334455" + i.ToString("x6")),
                ["values"] = new BsonArray { 0.1m, 0.1d },
                ["number"] = 0.1m,
                ["payload"] = new string((char)('a' + i % 26), 240) + i
            }).ToArray();
            var actual = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            actual.Length.Should().Be(expected.Length);
            for (var i = 0; i < expected.Length; i++)
                BsonSerializer.Serialize(actual[i]).Should().Equal(BsonSerializer.Serialize(expected[i]));
            // Expected ObjectId order comes from its hex bytes, not the engine comparer.
            rows.Query().OrderBy("$.oid").ToArray().Select(x => x["_id"].AsInt32).Should().Equal(
                expected.OrderBy(x => x["oid"].AsObjectId.ToString(), StringComparer.Ordinal).Select(x => x["_id"].AsInt32));
            foreach (var row in expected)
                rows.Find(Query.Parameterized.EQ("oid", row["oid"])).Single()["_id"].Should().Be(row["_id"]);
            foreach (var key in new BsonValue[] { 0.1m, 0.1d })
                rows.Find(BsonExpression.Create("$.values ANY = @0", key)).Select(x => x["_id"].AsInt32).OrderBy(x => x)
                    .Should().Equal(Enumerable.Range(1, 48));
            rows.Query().OrderBy("$.oid").GetPlan()["index"]["name"].AsString.Should().Be("oid");
            rows.Query().Where(BsonExpression.Create("$.values ANY = @0", 0.1m))
                .GetPlan()["index"]["name"].AsString.Should().Be("values");
            rows.Query().Where("IIF($.number = DOUBLE($.number), 1, 0) = 0")
                .GetPlan()["index"]["name"].AsString.Should().Be("computed");
            rows.Count("IIF($.number = DOUBLE($.number), 1, 0) = 1").Should().Be(0);
            rows.Count("IIF($.number = DOUBLE($.number), 1, 0) = 0").Should().Be(48);
            foreach (var row in expected)
            foreach (var key in new[] { row["payload"].AsString, row["payload"].AsString.ToUpperInvariant() })
            {
                var query = rows.Query().Where(BsonExpression.Create("MAP($.values[*] => IIF(@ = DOUBLE(@), UPPER($.payload), $.payload)) ANY = @0", key));
                query.GetPlan()["index"]["name"].AsString.Should().Be("flags");
                query.ToArray().Single()["_id"].Should().Be(row["_id"]);
            }
            db.GetCollection("cold").FindAll().Single()["payload"].AsString.Should().Be("untouched");
        }
    }
}
