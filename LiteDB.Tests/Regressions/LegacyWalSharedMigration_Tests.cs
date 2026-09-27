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
    /// Regression since 5.0.21: the first writable open migrates a legacy file and calls
    /// <c>_walIndex.Checkpoint(); _walIndex.Clear();</c> (IndexMigration.cs, fd5cc23bc) assuming
    /// the checkpoint drained the WAL. Since fc9cd5509 Checkpoint() is lease-aware: when the
    /// shared reader registry cannot be inspected (<c>SharedReaderVersions</c> returns null:
    /// unreadable/locked "-readers" directory, a malformed live lease, a slot file mid-append) it
    /// returns 0 without writing anything, and with a live lease it only backfills up to that
    /// version. Clear() then truncates the legacy WAL, silently discarding every committed
    /// transaction that 5.0.21 left in it, and the file is converted so 5.0.21 cannot open it.
    /// Shared (Connection=shared) and coordinated engines always set SharedReaderVersions.
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
            Recover(settings => { }).Should().Be((21, 101));
        }

        [Fact]
        public void Open_while_the_reader_registry_cannot_be_inspected_keeps_the_5_0_21_wal_commits()
        {
            Recover(settings => settings.SharedReaderVersions = () => null).Should().Be((21, 101));
        }

        [Fact]
        public void Open_with_a_live_reader_lease_keeps_the_5_0_21_wal_commits()
        {
            Recover(settings => settings.SharedReaderVersions = () => new[] { 1 }).Should().Be((21, 101));
        }

        private static (int updated, int count) Recover(Action<EngineSettings> configure)
        {
            using var data = Entry("crash.db");
            using var log = Entry("crash-log.db");

            var settings = new EngineSettings { DataStream = data, LogStream = log };
            configure(settings);
            using (var engine = new LiteEngine(settings))
            {
            }

            using var reopenedEngine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log });
            using var db = new LiteDatabase(reopenedEngine, disposeOnClose: false);
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
