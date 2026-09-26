using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2820_Tests
    {
        private const int PageSize = 8192;
        private const int LedgerSize = 96;
        private const string V4Password = "pass123";
        private const string V4Sha256 = "5c864394a02ca5a4e2712597fd8bf6ab0fb807c6f39ba158a1a6c5d4c82badf1";

        private static readonly string[] V4Ledger =
        {
            "{\"_id\":{\"$guid\":\"4ac8f759-248f-4114-8be6-e510ad4e140d\"},\"Name\":\"Jesse\"}",
            "{\"_id\":{\"$guid\":\"db503008-84d5-42d8-b372-d7616ea133f1\"},\"Name\":\"Bob\"}"
        };

        [Theory]
        [InlineData(16, false)]
        [InlineData(16, true)]
        [InlineData(8192, false)]
        [InlineData(8192, true)]
        [InlineData(20009, false)]
        [InlineData(20009, true)]
        public void Opening_a_foreign_file_rejects_it_without_changing_any_byte(int length, bool readOnly)
        {
            using var file = new TempFile();
            var original = ForeignBytes(length);
            var originalHash = Sha256(original);
            File.WriteAllBytes(file.Filename, original);

            var failure = Record.Exception(() =>
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename,
                    ReadOnly = readOnly
                });
                db.GetCollection("rows").Count();
            });

            var after = File.ReadAllBytes(file.Filename);
            using var exclusive = File.Open(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using (new AssertionScope())
            {
                failure.Should().BeOfType<LiteException>("a non-empty foreign file must be rejected");
                if (failure is LiteException lite)
                    lite.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                after.Should().HaveCount(original.Length, "format detection must not truncate or initialize the file");
                Sha256(after).Should().Be(originalHash, "even an in-place same-length edit is data loss");
                after.Should().Equal(original);
                exclusive.Length.Should().Be(original.Length, "failed construction must release the file handle intact");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rejecting_an_encrypted_v4_file_preserves_it_for_a_later_explicit_upgrade(bool readOnly)
        {
            var source = LoadResource("Issue_2494_EncryptedV4.db");
            source.Should().HaveCount(20480);
            Sha256(source).Should().Be(V4Sha256, "the real v4.1 fixture is the byte-level oracle");
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, source);

            var rejection = Record.Exception(() =>
            {
                using var db = OpenV4(file.Filename, false, readOnly);
                db.GetCollectionNames().ToArray();
            });

            var afterRejection = File.ReadAllBytes(file.Filename);
            using (File.Open(file.Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            using (new AssertionScope())
            {
                rejection.Should().BeOfType<LiteException>("v4 needs the explicit Upgrade option");
                if (rejection is LiteException lite)
                {
                    lite.ErrorCode.Should().NotBe(0);
                    lite.Message.Should().NotBeNullOrWhiteSpace();
                }
                afterRejection.Should().HaveCount(source.Length);
                Sha256(afterRejection).Should().Be(V4Sha256);
                afterRejection.Should().Equal(source,
                    "a rejected probe must leave every encrypted v4 byte available to the upgrader");
            }

            using (var upgraded = OpenV4(file.Filename, true, false))
            {
                PlayerLedger(upgraded).Should().Equal(V4Ledger);
                upgraded.GetCollection("issue2820_upgrade").Insert(new BsonDocument
                {
                    ["_id"] = 2820,
                    ["marker"] = "written only after the explicit upgrade"
                });
                upgraded.Checkpoint();
            }

            using var reopened = OpenV4(file.Filename, false, false);
            PlayerLedger(reopened).Should().Equal(V4Ledger);
            JsonSerializer.Serialize(reopened.GetCollection("issue2820_upgrade").FindById(2820))
                .Should().Be("{\"_id\":2820,\"marker\":\"written only after the explicit upgrade\"}");
        }

        [Fact]
        public void Writable_open_trims_a_torn_v5_tail_only_after_validating_and_preserves_the_ledger()
        {
            using var file = new TempFile();
            var checkpoint = CreateV5Fixture(file.Filename);
            var checkpointHash = Sha256(checkpoint);
            AppendTornTail(file.Filename);

            using (var db = new LiteDatabase(file.Filename))
            {
                var recovered = TempFile.ReadAllBytesShared(file.Filename);
                recovered.Should().Equal(checkpoint, "only the incomplete trailing page may be discarded");
                Sha256(recovered).Should().Be(checkpointHash);
                AssertV5Ledger(db, false);
                db.GetCollection("ledger").Insert(new BsonDocument
                {
                    ["_id"] = 2820,
                    ["marker"] = "post-recovery",
                    ["payload"] = "durable",
                    ["checksum"] = 2820 * 7919
                });
                db.Checkpoint();
            }

            var durableImage = File.ReadAllBytes(file.Filename);
            (durableImage.Length % PageSize).Should().Be(0);
            durableImage.Should().NotEqual(checkpoint, "the post-recovery sentinel must be persisted");
            using var reopened = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename,
                ReadOnly = true
            });
            AssertV5Ledger(reopened, true);
            File.ReadAllBytes(file.Filename).Should().Equal(durableImage);
        }

        [Fact]
        public void Read_only_open_recovers_a_torn_v5_tail_without_trimming_or_rewriting_it()
        {
            using var file = new TempFile();
            var checkpoint = CreateV5Fixture(file.Filename);
            var tail = AppendTornTail(file.Filename);
            var tornImage = checkpoint.Concat(tail).ToArray();
            var tornHash = Sha256(tornImage);

            using (var db = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename,
                ReadOnly = true
            }))
            {
                AssertV5Ledger(db, false);
                var whileOpen = File.ReadAllBytes(file.Filename);
                whileOpen.Should().HaveCount(tornImage.Length);
                Sha256(whileOpen).Should().Be(tornHash);
                whileOpen.Should().Equal(tornImage, "ReadOnly must make recovery logical rather than destructive");
            }

            var after = File.ReadAllBytes(file.Filename);
            Sha256(after).Should().Be(tornHash);
            after.Should().Equal(tornImage);
        }

        [Fact]
        public void Empty_new_files_remain_usable_as_databases()
        {
            using var file = new TempFile();
            File.WriteAllBytes(file.Filename, new byte[0]);
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "new" });
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").FindById(7)["value"].AsString.Should().Be("new");
        }

        private static byte[] CreateV5Fixture(string filename)
        {
            using (var db = new LiteDatabase(filename))
            {
                var collection = db.GetCollection("ledger");
                collection.InsertBulk(Enumerable.Range(1, LedgerSize).Select(id => new BsonDocument
                {
                    ["_id"] = id,
                    ["marker"] = Marker(id),
                    ["payload"] = Payload(id),
                    ["checksum"] = id * 7919
                }));
                collection.EnsureIndex("marker", true);
                db.Checkpoint();
            }

            var bytes = File.ReadAllBytes(filename);
            bytes.Length.Should().BeGreaterThan(PageSize * 16, "the recovery control must span many pages");
            (bytes.Length % PageSize).Should().Be(0);
            return bytes;
        }

        private static void AssertV5Ledger(LiteDatabase db, bool includeRecoveryRow)
        {
            var collection = db.GetCollection("ledger");
            var rows = collection.Find(Query.All("_id")).ToArray();
            rows.Should().HaveCount(LedgerSize + (includeRecoveryRow ? 1 : 0));
            rows.Take(LedgerSize).Select(x => x["_id"].AsInt32)
                .Should().Equal(Enumerable.Range(1, LedgerSize));

            foreach (var row in rows.Take(LedgerSize))
            {
                var id = row["_id"].AsInt32;
                row.Keys.OrderBy(x => x).Should().Equal("_id", "checksum", "marker", "payload");
                row["marker"].AsString.Should().Be(Marker(id));
                row["payload"].AsString.Should().Be(Payload(id));
                row["checksum"].AsInt32.Should().Be(id * 7919);
            }

            collection.FindOne(Query.EQ("marker", Marker(73)))["_id"].AsInt32.Should().Be(73);
            if (includeRecoveryRow)
            {
                var recovery = rows.Last();
                recovery["_id"].AsInt32.Should().Be(2820);
                recovery["marker"].AsString.Should().Be("post-recovery");
                recovery["payload"].AsString.Should().Be("durable");
                recovery["checksum"].AsInt32.Should().Be(2820 * 7919);
            }
        }

        private static byte[] AppendTornTail(string filename)
        {
            var tail = ForeignBytes(5003);
            using var stream = new FileStream(filename, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(tail, 0, tail.Length);
            stream.Flush();
            return tail;
        }

        private static byte[] ForeignBytes(int length)
        {
            var bytes = Enumerable.Range(0, length).Select(i => (byte)((i * 73 + 41) % 251)).ToArray();
            var marker = Encoding.UTF8.GetBytes("NOT-A-LITEDB-V5-FILE");
            Array.Copy(marker, bytes, Math.Min(marker.Length, bytes.Length));
            bytes[0] = 0;
            return bytes;
        }

        private static LiteDatabase OpenV4(string filename, bool upgrade, bool readOnly)
        {
            return new LiteDatabase(new ConnectionString
            {
                Filename = filename,
                Password = V4Password,
                Upgrade = upgrade,
                ReadOnly = readOnly
            });
        }

        private static string[] PlayerLedger(LiteDatabase db)
        {
            return db.GetCollection("PlayerDto").FindAll()
                .Select(x => JsonSerializer.Serialize(x))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
        }

        private static byte[] LoadResource(string filename)
        {
            var path = TestResource.GetPath(filename);
            File.Exists(path).Should().BeTrue("the repository fixture must exist at {0}", path);
            return File.ReadAllBytes(path);
        }

        private static string Marker(int id) => "original-" + id.ToString("D3", CultureInfo.InvariantCulture);

        private static string Payload(int id)
        {
            return new string((char)('A' + (id % 23)), 1700) + ":" + Marker(id);
        }

        private static string Sha256(byte[] bytes)
        {
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
