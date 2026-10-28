using System.IO;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("PromotionPowerLoss")]
    public class PromotionJournalAuthority_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public void Durable_journal_recovers_erased_identity_after_two_torn_repairs(string password, bool vector)
        {
            PromotionPowerLossScenario.Run(password, vector, "promotion-before-header-write",
                tornPrefix: 59, damage: true, recoveryPhase: "promotion-recovery-before-header-write",
                tearRecovery: true, inspectFiles: (data, wal) =>
                {
                    using var file = new TempFile();
                    var log = FileHelper.GetLogFile(file.Filename);
                    try
                    {
                        File.WriteAllBytes(file.Filename, data);
                        File.WriteAllBytes(log, wal);
                        foreach (var readOnly in new[] { true, true, false, false })
                        {
                            using (var db = new LiteDatabase(new ConnectionString
                            {
                                Filename = file.Filename, Password = password, ReadOnly = readOnly
                            })) PromotionPowerLossScenario.Verify(db, false, vector);
                            if (readOnly)
                            {
                                Assert.Equal(data, File.ReadAllBytes(file.Filename));
                                Assert.Equal(wal, File.ReadAllBytes(log));
                            }
                        }
                    }
                    finally { File.Delete(log); }
                });
        }
    }
}
