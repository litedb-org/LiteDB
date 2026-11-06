using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class Review3137g_Tests
    {
        [Fact]
        public void Partially_erased_ciphertext_creation_block_with_valid_redo_recovers()
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            const string password = "wal-secret";
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                // Preserve the preamble and every other ciphertext block. Tear only the
                // final byte of the block containing the immutable creation timestamp.
                primary[8192 + HeaderPage.P_CREATION_TIME / 16 * 16 + 15] = 0;
                var torn = ConvertedWalFiles.Plaintext(primary, password);
                var ticks = BitConverter.ToInt64(torn, HeaderPage.P_CREATION_TIME);
                ticks.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(DateTime.MaxValue.Ticks);
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, primary);
                    File.WriteAllBytes(log, wal);
                    using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = readOnly }))
                    {
                        db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                        db.GetCollection("newrows").FindAll().Single()["value"].AsString.Should().Be("checkpointed-modern-only");
                    }
                    if (readOnly)
                    {
                        File.ReadAllBytes(file.Filename).Should().Equal(primary);
                        File.ReadAllBytes(log).Should().Equal(wal);
                    }
                }
            }
            finally { File.Delete(log); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("wal-secret")]
        public void Partially_erased_creation_with_matching_generation_and_valid_redo_recovers(string password)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                primary = ConvertedWalFiles.Rewrite(primary, password, bytes => Array.Clear(bytes, HeaderPage.P_CREATION_TIME, 4));
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, primary);
                    File.WriteAllBytes(log, wal);
                    using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = readOnly }))
                    {
                        db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                        db.GetCollection("newrows").FindAll().Single()["value"].AsString.Should().Be("checkpointed-modern-only");
                    }
                    if (readOnly)
                    {
                        File.ReadAllBytes(file.Filename).Should().Equal(primary);
                        File.ReadAllBytes(log).Should().Equal(wal);
                    }
                }
            }
            finally { File.Delete(log); }
        }
    }
}
