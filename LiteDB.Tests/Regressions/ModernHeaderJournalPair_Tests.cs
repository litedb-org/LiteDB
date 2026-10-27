using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class ModernHeaderJournalPair_Tests
    {
        public static IEnumerable<object[]> LegacyPairs()
        {
            foreach (var password in new[] { null, "wal-secret" })
            foreach (var version in new byte[] { 8, 9 })
                yield return new object[] { password, version };
        }

        // Verifier F1: a real converted file gains a modern-only collection before
        // a later checkpoint fails before its first data write, leaving valid redo.
        [Theory]
        [MemberData(nameof(LegacyPairs))]
        public void Modern_journal_beside_legacy_primary_refuses_before_repair_or_truncation(string password, byte version)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                Capture(file.Filename, password, out var legacy, out _, out var wal);
                if (version != 8)
                    legacy = ConvertedWalFiles.Rewrite(legacy, password, bytes => bytes[HeaderPage.P_FILE_VERSION] = version);
                foreach (var readOnly in new[] { false, true })
                {
                    File.WriteAllBytes(file.Filename, legacy);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var error = Record.Exception(() => Open(file.Filename, password, readOnly).Dispose());
                        // Check bytes before diagnostics: even a failed open must
                        // preserve both the primary and the journal on every retry.
                        File.ReadAllBytes(file.Filename).Should().Equal(legacy);
                        File.Exists(log).Should().BeTrue();
                        File.ReadAllBytes(log).Should().Equal(wal);
                        var refusal = error.Should().BeOfType<LiteException>().Subject;
                        refusal.ErrorCode.Should().Be(LiteException.INVALID_DATABASE);
                        refusal.Message.Should().Contain("modern header journal").And.Contain("primary")
                            .And.Contain("preserve both files for recovery");
                    }
                }
            }
            finally { File.Delete(log); }
        }

        public static IEnumerable<object[]> ModernPairs()
        {
            foreach (var password in new[] { null, "wal-secret" })
            foreach (var damage in new[] { "none", "checksum", "identity", "salt", "salt-marker" })
                yield return new object[] { password, damage };
        }

        [Theory]
        [MemberData(nameof(ModernPairs))]
        public void Matching_modern_journal_recovers_committed_rows_and_modern_collection_twice(string password, string damage)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                Capture(file.Filename, password, out _, out var modern, out var wal);
                if (damage != "none")
                    modern = ConvertedWalFiles.Rewrite(modern, password, bytes =>
                    {
                        if (damage == "checksum") bytes[BasePage.P_TRANSACTION_ID] ^= 1;
                        // Even the version/format identity may tear; the preserved
                        // generation salt can still bind the primary to its journal.
                        if (damage == "identity")
                        {
                            bytes[HeaderPage.P_FILE_VERSION] = 0;
                            bytes[WalChecksum.MarkerPosition] ^= 1;
                        }
                        if (damage == "salt" || damage == "salt-marker") bytes[WalChecksum.SaltPosition] ^= 1;
                        if (damage == "salt-marker") bytes[WalChecksum.MarkerPosition] ^= 1;
                    });
                foreach (var readOnly in new[] { false, true })
                {
                    File.WriteAllBytes(file.Filename, modern);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using (var db = Open(file.Filename, password, readOnly))
                        {
                            db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(
                                new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString());
                            db.GetCollection("newrows").FindAll().Select(x => x.ToString()).Should().Equal(
                                new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" }.ToString());
                        }
                        if (readOnly)
                        {
                            File.ReadAllBytes(file.Filename).Should().Equal(modern);
                            File.ReadAllBytes(log).Should().Equal(wal);
                        }
                    }
                }
            }
            finally { File.Delete(log); }
        }

        private static LiteDatabase Open(string filename, string password, bool readOnly) => new LiteDatabase(new ConnectionString
        {
            Filename = filename, Password = password, ReadOnly = readOnly, LegacyIndexScan = readOnly
        });

        private static void Capture(string filename, string password, out byte[] legacy, out byte[] modern, out byte[] wal)
        {
            legacy = ConvertedWalFiles.Entry("LegacyWal_5_0_21.zip", password == null ? "plain.db" : "encrypted.db");
            File.WriteAllBytes(filename, legacy);
            using (var engine = new LiteEngine(new EngineSettings { Filename = filename, Password = password }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("newrows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "checkpointed-modern-only" });
                db.Checkpoint();
                db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
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
            journal.Position.Should().Be(2L * PAGE_SIZE);
            journal.IsPublished(ConvertedWalFiles.Plaintext(modern, password)).Should().BeTrue();
            journal.ValidateCheckpointWal(stream, journal.Header);
        }
    }
}
