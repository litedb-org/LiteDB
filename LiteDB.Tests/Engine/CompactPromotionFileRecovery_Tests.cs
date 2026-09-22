using System;
using System.IO;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("PromotionPowerLoss")]
    public class CompactPromotionFileRecovery_Tests
    {
        [Fact]
        public void Promotion_recovery_preserves_a_later_corruption_guard()
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                PromotionPowerLossScenario.Run(null, false, "promotion-after-header-flush",
                    inspectFiles: (data, log) =>
                    {
                        data[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
                        File.WriteAllBytes(file.Filename, data);
                        File.WriteAllBytes(logName, log);
                        using (var db = new LiteDatabase(file.Filename))
                            PromotionPowerLossScenario.Verify(db, false, false);
                        Assert.Equal(1, File.ReadAllBytes(file.Filename)[HeaderPage.P_INVALID_DATAFILE_STATE]);
                    });
            }
            finally { File.Delete(logName); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Damaged_journal_does_not_authorize_a_header_repair(string password)
        {
            using var file = new TempFile();
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                PromotionPowerLossScenario.Run(password, false, "promotion-before-header-write",
                    tornPrefix: 59, damage: true, inspectFiles: (data, log) =>
                    {
                        var damaged = (byte[])log.Clone();
                        damaged[damaged.Length - Constants.PAGE_SIZE + 128] ^= 1;
                        File.WriteAllBytes(file.Filename, data);
                        File.WriteAllBytes(logName, damaged);
                        Assert.Throws<LiteException>(() =>
                        {
                            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
                        });
                        Assert.Equal(data, File.ReadAllBytes(file.Filename));
                        Assert.Equal(damaged, File.ReadAllBytes(logName));
                    });
            }
            finally { File.Delete(logName); }
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Torn_header_recovers_through_real_files_without_touching_unrelated_files(string password, bool readOnly)
        {
            using var file = new TempFile();
            using var unrelated = new TempFile();
            var sentinel = new byte[] { 7, 3, 9, 1 };
            File.WriteAllBytes(unrelated.Filename, sentinel);
            var logName = FileHelper.GetLogFile(file.Filename);
            try
            {
                PromotionPowerLossScenario.Run(password, true, "promotion-before-header-write",
                    tornPrefix: 59, damage: true, keepVectorJournal: true, inspectFiles: (data, log) =>
                    {
                        File.WriteAllBytes(file.Filename, data);
                        File.WriteAllBytes(logName, log);
                        var connection = new ConnectionString { Filename = file.Filename, Password = password, ReadOnly = readOnly };
                        using (var db = new LiteDatabase(connection))
                        {
                            PromotionPowerLossScenario.Verify(db, false, true);
                            if (!readOnly) db.Checkpoint();
                        }
                        if (readOnly)
                        {
                            Assert.Equal(data, File.ReadAllBytes(file.Filename));
                            Assert.Equal(log, File.ReadAllBytes(logName));
                        }
                        var completed = File.ReadAllBytes(file.Filename);
                        using (var reopened = new LiteDatabase(connection))
                            PromotionPowerLossScenario.Verify(reopened, false, true);
                        Assert.Equal(completed, File.ReadAllBytes(file.Filename));
                        Assert.Equal(sentinel, File.ReadAllBytes(unrelated.Filename));
                    });
            }
            finally { File.Delete(logName); }
        }
    }
}
