using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    [Trait("Category", "IoSafety")]
    public class Review3137i_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("wal-secret", false)]
        [InlineData("wal-secret", true)]
        public void Version_and_both_modern_markers_torn_with_valid_same_database_redo_recovers(string password, bool zeroOnly)
        {
            using var file = new TempFile();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                ModernHeaderJournalPair_Tests.Capture(file.Filename, password, out _, out var primary, out var wal);
                primary = ConvertedWalFiles.Rewrite(primary, password, bytes =>
                {
                    bytes[HeaderPage.P_FILE_VERSION] = zeroOnly ? (byte)(bytes[HeaderPage.P_FILE_VERSION] & 8) : (byte)14;
                    bytes[BasePage.P_PAGE_FORMAT] = 0;
                    Array.Clear(bytes, WalChecksum.MarkerPosition, 4);
                    if (zeroOnly) Array.Clear(bytes, BasePage.P_TRANSACTION_ID, 4);
                    else bytes[BasePage.P_TRANSACTION_ID] ^= 1;
                });
                foreach (var readOnly in new[] { true, false })
                {
                    File.WriteAllBytes(file.Filename, primary);
                    File.WriteAllBytes(log, wal);
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var error = Record.Exception(() =>
                        {
                            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = readOnly });
                            db.GetCollection("rows").FindAll().Select(x => x.ToString()).Should().Equal(
                                new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString());
                            db.GetCollection("rows").Find(Query.EQ("_id", 1)).Single()["value"].AsInt32.Should().Be(42);
                            db.GetCollection("newrows").FindAll().Single()["value"].AsString.Should().Be("checkpointed-modern-only");
                        });
                        if (readOnly || error != null)
                        {
                            File.ReadAllBytes(file.Filename).Should().Equal(primary);
                            File.ReadAllBytes(log).Should().Equal(wal);
                        }
                        error.Should().BeNull("only the primary header was torn; its valid same-database redo survives");
                    }
                }
            }
            finally { File.Delete(log); }
        }
    }
}
