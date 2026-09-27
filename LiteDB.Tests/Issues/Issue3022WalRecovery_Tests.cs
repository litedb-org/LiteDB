using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue3022WalRecovery_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Opening_salvage_preserves_the_complete_data_WAL_pair(bool recoverImmediately)
        {
            using var file = Issue3022LegacyDamage_Tests.DamagedFile(true, wal: true);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            log.Length.Should().BeGreaterThan(0);
            var settings = new ConnectionString { Filename = file.Filename, AutoRebuild = recoverImmediately };
            if (!recoverImmediately)
            {
                Action open = () => new LiteDatabase(settings).Dispose();
                open.Should().Throw<LiteException>().Where(ex => ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE);
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
                settings.AutoRebuild = true;
            }
            using (var db = new LiteDatabase(settings))
            {
                db.GetCollection("rows").Count().Should().Be(3);
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 4, ["payload"] = "new" });
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetSuffixFile(logName, "-backup", false)).Should().Equal(log);
            for (var retry = 0; retry < 2; retry++)
            {
                using var db = new LiteDatabase(file.Filename);
                db.GetCollection("rows").Count().Should().Be(4);
                for (var id = 1; id <= 4; id++)
                    db.GetCollection("rows").FindById(id)["payload"].AsString.Should().Be(id == 4 ? "new" : "original-" + id);
                db.GetCollection("unrelated").FindById(1)["payload"].AsString.Should().Be("preserved");
                db.GetCollection("_rebuild_errors").Count().Should().Be(1);
            }
        }
    }
}
