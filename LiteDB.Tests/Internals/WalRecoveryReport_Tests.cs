using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class WalRecoveryReport_Tests
    {
        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void SharedReopen_PreservesTheLastRecoveryReport(string password, bool corrupt)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            source.Update("docs", 1);
            if (!corrupt) source.Database.BeginTrans();
            source.Update("docs", 2);
            if (!corrupt) source.Engine.GetMonitor().GetThreadTransaction().Safepoint();
            var wal = source.Log.ToArray();
            var preamble = password == null ? 0 : PAGE_SIZE;
            if (corrupt) wal[((wal.Length - preamble) / WalChecksum.FrameSize - 1) * WalChecksum.FrameSize + preamble + 400] ^= 1;
            using var file = new TempFile();
            var logPath = FileHelper.GetLogFile(file.Filename);
            File.WriteAllBytes(file.Filename, source.Data.ToArray());
            File.WriteAllBytes(logPath, wal);
            try
            {
                using var db = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = password, Connection = ConnectionType.Shared
                });
                // Recovery happens in this operation's short-lived engine.
                db.GetCollection("docs").FindAll().Should().HaveCount(WalTestDatabase.DocumentCount)
                    .And.OnlyContain(x => x["value"].AsInt32 == 1);
                var report = db.GetCollection("$database").FindAll().Single();
                report["recoveryDiscardedWalBytes"].AsInt64.Should().BeGreaterThan(0);
                report["recoveryInvalidWalTail"].AsBoolean.Should().Be(corrupt);
                db.Checkpoint();
                db.GetCollection("docs").Count().Should().Be(WalTestDatabase.DocumentCount);
                var later = db.GetCollection("$database").FindAll().Single();
                later["recoveryDiscardedWalBytes"].Should().Be(report["recoveryDiscardedWalBytes"]);
                later["recoveryInvalidWalTail"].Should().Be(report["recoveryInvalidWalTail"]);
                using var independent = new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = password, Connection = ConnectionType.Shared
                });
                independent.GetCollection("$database").FindAll().Single()["recoveryDiscardedWalBytes"].AsInt64.Should().Be(0);
            }
            finally { File.Delete(logPath); }
        }
    }
}
