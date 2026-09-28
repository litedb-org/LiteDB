#if DEBUG || TESTING
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A data file that stops syncing (#2242) after commits were acknowledged durable. No log sync
    /// of the engine's barriers makes an emptied WAL durable before a data sync succeeds
    /// (UnsyncedBackfillPowerLoss_Tests), and no sync outside them may either: a stream the
    /// engine opens to read never syncs.
    /// </summary>
    [Trait("Category", "IoSafety")]
    [Collection(NativeFileSyncCollection.Name)]
    public class DataFileStopsSyncing_Tests
    {
        /// <summary>
        /// An encrypted stream synced its file when created, also one a pool creates for a concurrent
        /// read: outside the engine's barriers, it could make an emptied WAL durable ahead of its
        /// backfill. Only a writer syncs its preamble now; concurrent readers sync nothing.
        /// </summary>
        [Fact]
        public void Encrypted_readers_do_not_sync_the_files()
        {
            using var file = new TempFile();
            using var power = new SyncPowerLossModel(file.Filename);
            var settings = power.Settings();
            settings.Password = "secret";
            using var engine = new LiteEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 1)));
            var (dataSyncs, logSyncs) = (power.DataSyncs, power.LogSyncs);

            using var barrier = new Barrier(6);
            var readers = Enumerable.Range(0, 6).Select(_ => new Thread(() =>
            {
                using var reader = engine.Query("rows", new Query());
                reader.Read().Should().BeTrue();
                barrier.SignalAndWait(TimeSpan.FromSeconds(20)).Should().BeTrue();
            })).ToArray();
            foreach (var reader in readers) reader.Start();
            foreach (var reader in readers) reader.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

            (power.DataSyncs, power.LogSyncs).Should().Be((dataSyncs, logSyncs), "six concurrent readers open streams of their own");
        }

        private const int Rows = 64;

        private static void Setup(string filename)
        {
            using var setup = new LiteDatabase(filename);
            setup.GetCollection("rows").Insert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, 0)));
        }

        private static void Update(LiteDatabase db, int value) =>
            db.GetCollection("rows").Upsert(Enumerable.Range(1, Rows).Select(id => MvccRetirementScenario.Document(id, value)));

        private static int[] Values(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Distinct().ToArray();

        private static bool DurableLogFlush(LiteDatabase db) =>
            db.GetCollection("$database").FindAll().Single()["durableLogFlush"].AsBoolean;

        private static int AssertLegacyCommits(LiteDatabase db)
        {
            var docs = db.GetCollection("docs").FindAll().ToList();
            docs.Should().HaveCount(101);
            docs.Count(x => x["value"].AsInt32 == 7).Should().Be(21);
            return docs.Count;
        }

        private static byte[] Entry(string name)
        {
            using var resource = typeof(DataFileStopsSyncing_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.WalCrash_5_0_21.zip");
            using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
            using var entry = zip.GetEntry(name).Open();
            using var bytes = new MemoryStream();
            entry.CopyTo(bytes);
            return bytes.ToArray();
        }
    }
}
#endif
