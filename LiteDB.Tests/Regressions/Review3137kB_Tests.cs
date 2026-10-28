using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class Review3137kB_Tests
    {
        private static string Evidence => Environment.GetEnvironmentVariable("REVIEW_KB_OUT");

        [Fact]
        public void Released_primary_and_header_body_tears_beside_foreign_journal_refuse_unchanged()
        {
            var failures = new List<string>();
            var inputs = new List<(string name, byte[] data, string password)>();
            var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Resources"));
            foreach (var name in new[] { "v4.db", "Issue_2494_EncryptedV4.db", "Issue2255_Before2753.db", "Issue2417_MyData.db", "Issue2417_TestCacheDb.db" })
                inputs.Add((name, File.ReadAllBytes(Path.Combine(resources, name)), null));
            foreach (var archive in new[] { "LegacyWal_5_0_21.zip", "IndexMigration_5_0_21.zip" })
            foreach (var password in new[] { (string)null, archive == "LegacyWal_5_0_21.zip" ? "wal-secret" : "migration-power-loss" })
                inputs.Add((archive + "/" + (password == null ? "plain" : "encrypted"), Entry(archive, password == null ? "plain.db" : "encrypted.db"), password));
            foreach (var name in new[] { "stale.db", "indexes.db", "stale-encrypted.db", "indexes-encrypted.db" })
                inputs.Add(("LegacyKeys/" + name, Entry("IndexMigrationLegacyKeys_5_0_21.zip", name), name.Contains("encrypted") ? "secret" : null));
            var corpus = Environment.GetEnvironmentVariable("REVIEW_KB_CORPUS");
            if (corpus != null)
            foreach (var path in Directory.GetFiles(corpus, "*.db"))
                inputs.Add((Path.GetFileName(path), File.ReadAllBytes(path), path.Contains("encrypted") && path.Contains("5.0") ? "release-corpus" : null));
            foreach (var input in inputs)
            {
                using var file = new TempFile();
                var log = FileHelper.GetLogFile(file.Filename);
                try
                {
                    Capture(file.Filename, input.password, out _, out var wal);
                    foreach (var damage in new[] { "intact", "crc", "version", "signature", "header-zero", "crc-body-zero", "crc-body-prefix", "version-body-zero", "version-body-prefix" })
                    {
                        // v4 has no modern AES wrapper: its version-7 header remains raw.
                        var data = input.password == null ? (byte[])input.data.Clone() : Rewrite(input.data, input.password, _ => { });
                        Action<byte[]> tear = b =>
                        {
                            if (damage.StartsWith("crc")) b[BasePage.P_TRANSACTION_ID] ^= 1;
                            if (damage.StartsWith("version")) b[HeaderPage.P_FILE_VERSION] = 0;
                            if (damage == "signature") b[HeaderPage.P_HEADER_INFO] = 0;
                            if (damage == "header-zero") Array.Clear(b, 0, PAGE_SIZE);
                            if (damage.EndsWith("body-zero")) Array.Clear(b, PAGE_SIZE, PAGE_SIZE);
                            if (damage.EndsWith("body-prefix")) Array.Clear(b, PAGE_SIZE + BasePage.P_PAGE_ID, 4);
                        };
                        if (input.password == null) tear(data); else data = Rewrite(data, input.password, tear);
                        foreach (var readOnly in new[] { true, false })
                        {
                            File.WriteAllBytes(file.Filename, data);
                            File.WriteAllBytes(log, wal);
                            for (var attempt = 0; attempt < 2; attempt++)
                            {
                                var error = Record.Exception(() => Open(file.Filename, input.password, readOnly).Dispose());
                                var dc = !File.ReadAllBytes(file.Filename).SequenceEqual(data);
                                var wc = !File.Exists(log) || !File.ReadAllBytes(log).SequenceEqual(wal);
                                var row = $"foreign\t{input.name}\t{damage}\t{readOnly}\t{attempt}\t{error?.GetType().Name ?? "OK"}\t{dc}\t{wc}";
                                Save(row);
                                if (Evidence != null && dc && input.name == "LegacyWal_5_0_21.zip/plain" &&
                                    damage == "version-body-prefix" && !readOnly && attempt == 0)
                                {
                                    File.WriteAllBytes(Evidence + ".before.db", data);
                                    File.WriteAllBytes(Evidence + ".before-log.db", wal);
                                    File.WriteAllBytes(Evidence + ".after.db", File.ReadAllBytes(file.Filename));
                                    if (File.Exists(log)) File.WriteAllBytes(Evidence + ".after-log.db", File.ReadAllBytes(log));
                                }
                                if (error == null || dc || wc) failures.Add(row);
                            }
                        }
                    }
                }
                finally { File.Delete(log); }
            }
            failures.Should().BeEmpty("foreign recovery must preserve both files; details in REVIEW_KB_OUT");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Torn_modern_header_and_redo_covered_nonheader_pages_recover(string password)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                Capture(file.Filename, password, out var data, out var wal);
                foreach (var damage in new[] { "zero-data", "prefix-data", "zero-two-data-pages" })
                {
                    var torn = Rewrite(data, password, b =>
                    {
                        b[HeaderPage.P_FILE_VERSION] = 0;
                        b[BasePage.P_PAGE_FORMAT] = 0;
                        Array.Clear(b, WalChecksum.MarkerPosition, 4);
                        Array.Clear(b, BasePage.P_TRANSACTION_ID, 4);
                        // WAL covers the two updated data pages, 3 and 6.
                        if (damage == "prefix-data") Array.Clear(b, 3 * PAGE_SIZE, PAGE_HEADER_SIZE);
                        else Array.Clear(b, 3 * PAGE_SIZE, PAGE_SIZE);
                        if (damage == "zero-two-data-pages") Array.Clear(b, 6 * PAGE_SIZE, PAGE_SIZE);
                    });
                    foreach (var readOnly in new[] { true, false })
                    {
                        File.WriteAllBytes(file.Filename, torn);
                        File.WriteAllBytes(log, wal);
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            using (var db = Open(file.Filename, password, readOnly))
                            {
                                db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(
                                    new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString());
                                db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                                db.GetCollection("newrows").FindAll().Single()["value"].AsString.Should().Be("checkpointed-modern-only");
                            }
                            if (readOnly)
                            {
                                File.ReadAllBytes(file.Filename).Should().Equal(torn);
                                File.ReadAllBytes(log).Should().Equal(wal);
                            }
                            Save($"same\t{password ?? "plain"}\t{damage}\t{readOnly}\t{attempt}\tOK");
                        }
                    }
                }
            }
            finally { File.Delete(log); }
        }

        private static void Save(string row)
        {
            if (Evidence != null) File.AppendAllText(Evidence, row + Environment.NewLine);
        }

        private static LiteDatabase Open(string filename, string password, bool readOnly) => new LiteDatabase(new ConnectionString
        {
            Filename = filename, Password = password, ReadOnly = readOnly, LegacyIndexScan = readOnly
        });

        private static void Capture(string filename, string password, out byte[] modern, out byte[] wal)
        {
            using (var engine = new LiteEngine(new EngineSettings { Filename = filename, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
                db.GetCollection("newrows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" });
                db.Checkpoint();
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
                db.GetCollection("newrows").Update(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" });
                engine.SimulateDataWriteFail = page => throw new IOException("capture modern journal");
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>().WithMessage("capture modern journal");
            }
            modern = File.ReadAllBytes(filename);
            wal = File.ReadAllBytes(FileHelper.GetLogFile(filename));
            using var raw = new MemoryStream(wal);
            using var factory = new StreamFactory(raw, password);
            using var stream = factory.GetStream(false, false);
            var journal = HeaderJournal.Read(stream);
            journal.Should().NotBeNull();
            journal.Legacy.Should().BeFalse();
            journal.ValidateCheckpointWal(stream, journal.Header);
        }

        private static byte[] Entry(string archive, string name)
        {
            using var resource = typeof(Review3137kB_Tests).Assembly.GetManifestResourceStream("LiteDB.Tests.Resources." + archive);
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }

        private static byte[] Rewrite(byte[] bytes, string password, Action<byte[]> change)
        {
            byte[] plain;
            using (var raw = new MemoryStream(bytes))
            using (var factory = new StreamFactory(raw, password))
            using (var stream = factory.GetStream(false, false))
            using (var output = new MemoryStream())
            {
                stream.CopyTo(output);
                plain = output.ToArray();
            }
            change(plain);
            using var result = new MemoryStream();
            result.Write(bytes, 0, bytes.Length);
            using (var factory = new StreamFactory(result, password))
            using (var stream = factory.GetStream(true, false)) stream.Write(plain, 0, plain.Length);
            return result.ToArray();
        }
    }
}
