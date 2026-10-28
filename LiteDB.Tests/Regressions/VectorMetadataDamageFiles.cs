using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Vector;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    // Only the collection metadata is damaged; every checksummed page retains a valid CRC.
    internal static class VectorMetadataDamageFiles
    {
        internal static string Connection(string filename, string password) =>
            $"Filename={filename}" + (password == null ? "" : $";Password={password}");

        internal static byte[] Read(string filename, string password)
        {
            // A checkpointed database may still own its writable handle on Windows.
            using var raw = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var encrypted = password == null ? null : new AesStream(password, raw, allowRecovery: false);
            var input = (Stream)encrypted ?? raw;
            var bytes = new byte[input.Length];
            input.ReadRequired(bytes, 0, bytes.Length);
            return bytes;
        }

        internal static void Write(string filename, string password, byte[] bytes)
        {
            if (password == null)
            {
                File.WriteAllBytes(filename, bytes);
                return;
            }
            using var raw = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite);
            using var encrypted = new AesStream(password, raw, allowRecovery: false);
            encrypted.Write(bytes, 0, bytes.Length);
            encrypted.Flush();
        }

        internal static void LegacyFixture(string filename, string password)
        {
            using var zip = new ZipArchive(File.OpenRead(ArtifactFixtures.Path("Vectors_6_0_0_prerelease_114.zip")));
            using var entry = zip.GetEntry(password == null ? "vectors.db" : "vectors-encrypted.db").Open();
            using var output = File.Create(filename);
            entry.CopyTo(output);
        }

        internal static void Create(string filename, string password, string state, VectorDistanceMetric metric)
        {
            if (state != "current")
            {
                LegacyFixture(filename, password);
                Read(filename, password)[HeaderPage.P_FILE_VERSION].Should().Be(8);
                if (state == "legacy") return;
            }

            using (var db = new LiteDatabase(Connection(filename, password)))
            {
                var docs = db.GetCollection("docs");
                if (state == "migrated")
                {
                    docs.DropIndex("embedding").Should().BeTrue();
                    docs.DeleteAll().Should().Be(40);
                }
                docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, metric));
                docs.Insert(VectorDocument(1, 100f, 0f));
                docs.Insert(VectorDocument(2, 1f, 1f));
                db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 7, ["value"] = "preserve me" });
                db.Checkpoint();
            }
            Read(filename, password)[EnginePragmas.P_INDEX_ORDER_VERSION].Should().Be(EnginePragmas.INDEX_ORDER_VERSION);
        }

        internal static BsonDocument VectorDocument(int id, float x, float y) => new BsonDocument
        {
            ["_id"] = id, ["Embedding"] = new BsonVector(new[] { x, y })
        };

        internal static void Damage(string filename, string password, string kind)
        {
            var bytes = Read(filename, password);
            for (var start = PAGE_SIZE; start < bytes.Length; start += PAGE_SIZE)
            {
                if (bytes[start + BasePage.P_PAGE_TYPE] != (byte)PageType.Collection) continue;
                var page = new CollectionPage(new PageBuffer(bytes, start, 0));
                var index = page.GetCollectionIndex("embedding");
                if (index == null) continue;
                var metadata = page.GetVectorIndexMetadata("embedding");
                metadata.Should().NotBeNull("the source image must contain healthy vector metadata");
                using var reader = new BufferReader(new BufferSlice(bytes,
                    start + CollectionPage.P_INDEXES, PAGE_SIZE - CollectionPage.P_INDEXES));
                var count = reader.ReadByte();
                for (var i = 0; i < count; i++) _ = new CollectionIndex(reader);
                var section = start + CollectionPage.P_INDEXES + (int)reader.Position;
                reader.ReadByte().Should().Be(1);
                reader.ReadCString().Should().Be("embedding");
                var body = start + CollectionPage.P_INDEXES + (int)reader.Position;
                switch (kind)
                {
                    case "malformed": bytes[section + 1] = 0xFF; break; // invalid UTF-8
                    case "incomplete": bytes[section] = 0; break; // declared index without an entry
                    case "duplicated":
                        bytes[section] = 2;
                        var length = body - section - 1 + VectorIndexMetadata.GetLength();
                        Array.Copy(bytes, section + 1, bytes, section + 1 + length, length);
                        break;
                    case "slot": bytes[body] = 255; break;
                    case "dimension": bytes[body + 1] = bytes[body + 2] = 0; break;
                    case "metric": bytes[body + 3] = 255; break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind));
                }
                var buffer = new PageBuffer(bytes, start, 0);
                if (bytes[HeaderPage.P_FILE_VERSION] >= HeaderPage.CHECKSUM_FILE_VERSION)
                {
                    PageChecksum.Write(buffer);
                    PageChecksum.Validate(buffer, start);
                }
                Write(filename, password, bytes);
                return;
            }
            throw new InvalidOperationException("No collection page lists embedding.");
        }

        internal static byte[] LogBytes(string filename) => File.Exists(FileHelper.GetLogFile(filename))
            ? File.ReadAllBytes(FileHelper.GetLogFile(filename)) : Array.Empty<byte>();
    }
}
