using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Regression since 5.0.21: the first writable open migrates a legacy file and called
    /// <c>_walIndex.Checkpoint(); _walIndex.Clear();</c> (IndexMigration.cs, fd5cc23bc) assuming
    /// the checkpoint drained the WAL. Since fc9cd5509 Checkpoint() is lease-aware: when the
    /// shared reader registry cannot be inspected (<c>SharedReaderVersions</c> returns null:
    /// unreadable/locked "-readers" directory, a malformed live lease, a slot file mid-append) it
    /// returns 0 without writing anything, and with a live lease it only backfills up to that
    /// version. Clear() then truncated the legacy WAL, silently discarding every committed
    /// transaction that 5.0.21 left in it, and the file was converted so 5.0.21 could not open it.
    /// Conversion now drains the legacy WAL completely or refuses the open (LOCK_TIMEOUT) without
    /// changing either file.
    ///
    /// Fixture WalCrash_5_0_21.zip is a process-crash image written by the LiteDB 5.0.21 package:
    /// 100 documents {_id, value: 0} checkpointed, then 20 updates to value 7 and one insert
    /// (_id 100) committed to the WAL only. 5.0.21 and a direct HEAD open recover all of them.
    /// </summary>
    [Trait("Category", "RegressionSince5021")]
    public class LegacyWalSharedMigration_Tests
    {
        [Fact]
        public void Direct_open_recovers_the_5_0_21_wal_control()
        {
            using var data = Entry("crash.db");
            using var log = Entry("crash-log.db");
            Recover(data, log).Should().Be((21, 101));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Blocked_conversion_changes_nothing_and_recovers_once_unblocked(bool liveLease)
        {
            using var data = Entry("crash.db");
            using var log = Entry("crash-log.db");
            var originalData = data.ToArray();
            var originalLog = log.ToArray();
            var settings = new EngineSettings
            {
                DataStream = data, LogStream = log,
                SharedReaderVersions = liveLease ? () => new[] { 1 } : (Func<int[]>)(() => null)
            };

            Action open = () => new LiteEngine(settings).Dispose();
            open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);

            data.ToArray().Should().Equal(originalData, "a refused conversion must not touch the data file");
            log.ToArray().Should().Equal(originalLog, "a refused conversion must keep every legacy WAL frame");
            Recover(data, log).Should().Be((21, 101));
        }

        [Fact]
        public void Shared_connection_with_an_unreadable_reader_registry_keeps_the_5_0_21_wal()
        {
            using var file = new TempFile();
            using (var source = Entry("crash.db")) File.WriteAllBytes(file.Filename, source.ToArray());
            using (var source = Entry("crash-log.db")) File.WriteAllBytes(FileHelper.GetLogFile(file.Filename), source.ToArray());
            var originalData = File.ReadAllBytes(file.Filename);
            var originalLog = File.ReadAllBytes(FileHelper.GetLogFile(file.Filename));

            var settings = new EngineSettings
            {
                Filename = file.Filename,
                SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("registry denied")
            };
            using (var shared = new SharedEngine(settings))
            {
                Action query = () =>
                {
                    using var reader = shared.Query("docs", new Query());
                    while (reader.Read()) { }
                };
                query.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            }

            File.ReadAllBytes(file.Filename).Should().Equal(originalData);
            File.ReadAllBytes(FileHelper.GetLogFile(file.Filename)).Should().Equal(originalLog);

            using var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
            var docs = db.GetCollection("docs").FindAll().ToList();
            docs.Should().HaveCount(101);
            docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
        }

        private static (int updated, int count) Recover(MemoryStream data, MemoryStream log)
        {
            using var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var docs = db.GetCollection("docs").FindAll().ToList();
            return (docs.Count(x => x["value"].AsInt32 == 7), docs.Count);
        }

        private static MemoryStream Entry(string name)
        {
            using var resource = typeof(LegacyWalSharedMigration_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.WalCrash_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            bytes.Position = 0;
            return bytes;
        }
    }
}
