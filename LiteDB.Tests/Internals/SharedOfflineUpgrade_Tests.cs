#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOfflineUpgrade_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "offline-upgrade-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private EngineSettings Settings => new EngineSettings { Filename = Filename, SharedOneFile = true };

        public SharedOfflineUpgrade_Tests()
        {
            Directory.CreateDirectory(_directory);
            using var db = new LiteDatabase(Filename);
            db.GetCollection("docs").Insert(Enumerable.Range(1, 64).Select(id => new BsonDocument
                { ["_id"] = id, ["value"] = id * 7, ["payload"] = new string('x', 100) }));
            db.GetCollection("docs").EnsureIndex("value");
        }

        [OneFileTheory]
        [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
        [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
        public async Task Every_retirement_cut_and_interrupted_retry_preserve_database_and_wal(int cut)
        {
            // offline-validated, offline-retired, offline-readers-retired,
            // offline-complete are all reached by the child via CreationStage.
            Seed();
            var data = File.ReadAllBytes(Filename);
            var wal = FileHelper.GetLogFile(Filename);
            File.WriteAllBytes(wal, new byte[] { 23, 42, 97 }); // transition must never interpret WAL
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var child = new MvccProcess("offline-upgrade-cut", Filename, null, (attempt == 0 ? cut : 1).ToString());
                await child.Expect("ready");
                await child.Kill();
                File.ReadAllBytes(Filename).Should().Equal(data);
                File.ReadAllBytes(wal).Should().Equal(23, 42, 97);
            }
            for (var repeat = 0; repeat < 3; repeat++) Upgrade();
            Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName).Should().BeEquivalentTo("test.db", Path.GetFileName(wal));
            File.ReadAllBytes(Filename).Should().Equal(data);
            File.Delete(wal);
            for (var repeat = 0; repeat < 2; repeat++) Verify();
        }

        [OneFileFact]
        public async Task Live_legacy_peer_refuses_then_offline_upgrade_and_new_legacy_open_are_safe()
        {
            using (var peer = new MvccProcess("offline-legacy-hold", Filename, null))
            {
                await peer.Expect("ready");
                var before = Snapshot();
                Action open = () => { using var engine = new SharedEngine(Settings); };
                open.Should().Throw<IOException>();
                AssertSnapshot(before);
                await peer.Finish(release: true);
            }
            Seed();
            Upgrade();
            using (var mapped = new SharedEngine(Settings))
                await MvccProcess.Run("direct-host-shared-refused", Filename, null);
            // Native family locks allow sequential legacy reuse after mapped close.
            using (var legacy = new SharedEngine(new EngineSettings { Filename = Filename }))
            using (var db = new LiteDatabase(legacy, disposeOnClose: false)) db.GetCollection("docs").Count().Should().Be(64);
            Verify();
        }

        [OneFileTheory]
        [InlineData("read-only")] [InlineData("network")] [InlineData("unknown-volume")]
        [InlineData("unknown-reader")] [InlineData("unknown-state")] [InlineData("unknown-marker")]
        public void Refusal_preserves_every_byte_and_directory_entry(string reason)
        {
            Seed();
            if (reason == "unknown-reader") File.WriteAllText(Path.Combine(Filename + "-readers", "notes.txt"), "preserve");
            if (reason == "unknown-state") File.WriteAllText(Filename + "-shared-state", "preserve");
            if (reason == "unknown-marker") File.WriteAllText(Filename + "-shared-disabled", "preserve");
            var before = Snapshot();
            var settings = Settings;
            settings.ReadOnly = reason == "read-only";
            if (reason == "network" || reason == "unknown-volume") DatabaseFileIdentity.UnsupportedVolume = _ => true;
            try
            {
                Action open = () => { using var engine = new SharedEngine(settings); };
                open.Should().Throw<IOException>();
            }
            finally { DatabaseFileIdentity.UnsupportedVolume = null; }
            AssertSnapshot(before);
        }

        [OneFileFact]
        public void Dms_owner_excludes_retirement_and_unknown_shm_is_preserved()
        {
            Seed();
            using (var dms = new DatabaseFileLock(Filename + "-shm", false, false))
            {
                dms.Lock(SharedMemoryLayout.Dms, false);
                var before = Snapshot();
                Action upgrade = Upgrade;
                upgrade.Should().Throw<IOException>();
                AssertSnapshot(before);
            }
            File.WriteAllText(Filename + "-shm", "unknown authority");
            Action open = () => { using var engine = new SharedEngine(Settings); };
            open.Should().Throw<IOException>();
            File.ReadAllText(Filename + "-shm").Should().Be("unknown authority");
        }

        [OneFileFact]
        public void Every_subset_of_resurrected_ephemeral_entries_retires_repeatably()
        {
            Seed();
            var original = Snapshot().Where(p => p.Key != Filename).ToArray();
            var data = File.ReadAllBytes(Filename);
            // Model independently persisted directory deletions: every subset of
            // six original entries can reappear, including orphan page/content.
            for (var image = 0; image < (1 << original.Length); image++)
            {
                Directory.CreateDirectory(Filename + "-readers");
                for (var i = 0; i < original.Length; i++)
                    if ((image & (1 << i)) != 0) File.WriteAllBytes(original[i].Key, original[i].Value);
                Upgrade();
                Upgrade();
                File.ReadAllBytes(Filename).Should().Equal(data);
            }
            Verify();
        }

        private void Upgrade()
        {
            var settings = Settings;
            using var admission = NativeAdmission.Open(settings, true);
            SharedOfflineUpgrade.Run(settings, admission);
        }
        private void Seed()
        {
            var header = new SharedCoordinationProtocol.Header(SharedCoordinationProtocol.CreateParticipation(Filename), false);
            File.WriteAllBytes(Filename + "-shared-live", header.Bytes);
            File.WriteAllBytes(Filename + "-shared-state", SharedCoordinationProtocol.CreatePage(header));
            File.WriteAllBytes(Filename + "-shared-disabled", BitConverter.GetBytes(SharedCoordinationProtocol.Magic));
            File.WriteAllBytes(Filename + "-shm", SharedCoordinationProtocol.CreatePage(header));
            Directory.CreateDirectory(Filename + "-readers");
            var name = Path.Combine(Filename + "-readers", "slots-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(name + ".lease", Array.Empty<byte>());
            File.WriteAllBytes(name + ".slots", new byte[8]);
        }
        private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, File.ReadAllBytes);
        private void AssertSnapshot(Dictionary<string, byte[]> before)
        {
            var after = Snapshot();
            after.Keys.Should().BeEquivalentTo(before.Keys);
            foreach (var item in before) after[item.Key].Should().Equal(item.Value);
            Directory.Exists(Filename + "-readers").Should().BeTrue();
        }
        private void Verify()
        {
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.GetCollection("docs").Count().Should().Be(64);
            for (var id = 1; id <= 64; id++)
                db.GetCollection("docs").FindOne(Query.EQ("value", id * 7))["_id"].AsInt32.Should().Be(id);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
