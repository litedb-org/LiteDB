using System.IO;
using System.Linq;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// WAL recovery performed by a Shared handle's own core belongs to its connection's recovery
    /// report, as it does when an ordinary call performs it.
    /// </summary>
    public class TransactionHandleSharedRecovery_Tests
    {
        [Theory]
        [InlineData(null, false, false)]
        [InlineData(null, true, false)]
        [InlineData("secret", true, false)]
        [InlineData(null, true, true)]
        public void Recovery_report_survives_a_handle_first_open(string password, bool corrupt, bool ordinaryFirst)
        {
            using var source = new WalTestDatabase(password);
            source.Seed("docs");
            source.Database.Checkpoint();
            source.Update("docs", 1);
#pragma warning disable CS0618
            if (!corrupt) source.Database.BeginTrans();
#pragma warning restore CS0618
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
                using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password, Connection = ConnectionType.Shared });
                if (ordinaryFirst) Assert.Equal(WalTestDatabase.DocumentCount, db.GetCollection("docs").Count());
                else
                    using (var tx = db.BeginTransaction())
                    {
                        Assert.Equal(WalTestDatabase.DocumentCount, tx.GetCollection("docs").Count());
                        tx.Commit();
                    }
                var report = db.GetCollection("$database").FindAll().Single();
                Assert.True(report["recoveryDiscardedWalBytes"].AsInt64 > 0, "The recovery report of the handle's open was lost.");
                Assert.Equal(corrupt, report["recoveryInvalidWalTail"].AsBoolean);
            }
            finally { File.Delete(logPath); }
        }
    }
}
