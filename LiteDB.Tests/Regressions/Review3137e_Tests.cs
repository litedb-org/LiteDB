using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    public class Review3137e_Tests
    {
        [Theory]
        [InlineData(null, 7)]
        [InlineData(null, 8)]
        [InlineData(null, 9)]
        [InlineData(null, 14)]
        [InlineData("wal-secret", 7)]
        [InlineData("wal-secret", 8)]
        [InlineData("wal-secret", 9)]
        [InlineData("wal-secret", 14)]
        public void Torn_version_with_matching_nonzero_salt_and_valid_redo_recovers(string password, byte version)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                primary = ConvertedWalFiles.Rewrite(primary, password, bytes => bytes[HeaderPage.P_FILE_VERSION] = version);
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
