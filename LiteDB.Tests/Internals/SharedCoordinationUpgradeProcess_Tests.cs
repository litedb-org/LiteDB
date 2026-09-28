#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    public class SharedCoordinationUpgradeProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-abi-process-" + Guid.NewGuid().ToString("N"));
        private bool _passed;
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedCoordinationUpgradeProcess_Tests()
        {
            Directory.CreateDirectory(_directory);
            _directory = DatabaseFileIdentity.CanonicalPath(_directory);
        }

        public static System.Collections.Generic.IEnumerable<object[]> UpgradeBoundaries()
        {
            foreach (var password in new[] { null, "secret" })
            {
                foreach (var suffix in new[] { "-shared-live", "-shared-state" })
                    foreach (var stage in new[] { "created", "written", "flushed", "published" })
                        yield return new object[] { suffix, stage, password };
                foreach (var suffix in new[] { "-shared-state", "-shared-disabled", "-shared-live" })
                    yield return new object[] { suffix, "retired", password };
            }
        }

        [MappedTheory]
        [MemberData(nameof(UpgradeBoundaries))]
        public async Task Repeated_death_during_upgrade_and_recovery_preserves_committed_data(string suffix, string stage, string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            SeedLegacy();
            await KillAt(suffix + ":" + stage);
            // Interrupt recovery of the interrupted upgrade again, after publication.
            await KillAt("-shared-state:published");
            VerifyMapped(password, 0);
            await MvccProcess.Run("write", Filename, password, "7");
            await MvccProcess.Run("checkpoint", Filename, password);
            VerifyCold(password, 7);
            VerifyMapped(password, 7); // A completed transition is safe to open again.
            VerifyCold(password, 7);

            async Task KillAt(string boundary)
            {
                using var child = new MvccProcess("mapped-control-upgrade", Filename, password, boundary);
                await child.Expect("ready");
                await child.Kill();
            }
        }

        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task A_live_legacy_participant_blocks_upgrade_until_native_death(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            SeedLegacy();
            var old = File.ReadAllBytes(Filename + "-shared-state");
            using (var holder = new MvccProcess("mapped-legacy-holder", Filename, password))
            {
                await holder.Expect("ready");
                using (var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password }))
                using (var database = new LiteDatabase(engine))
                {
                    for (var i = 0; i < 3; i++) database.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(0);
                    Authority(engine).Should().BeNull();
                    engine.CoordinationFallbackReason.Should().Contain("protocol");
                }
                File.ReadAllBytes(Filename + "-shared-state").Should().Equal(old);
                await holder.Kill();
            }
            VerifyMapped(password, 0);
            await MvccProcess.Run("write", Filename, password, "7");
            await MvccProcess.Run("checkpoint", Filename, password);
            VerifyCold(password, 7);
        }

        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Newer_authority_falls_back_without_overwriting_its_files(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            using (var page = SharedCoordinationPage.Open(Filename)) page.Opened(123);
            var path = Filename + "-shared-state";
            var bytes = File.ReadAllBytes(path);
            SharedCoordinationProtocol.Write(bytes, 8, 2);
            File.WriteAllBytes(path, bytes);
            var live = File.ReadAllBytes(Filename + "-shared-live");
            await MvccProcess.Run("write", Filename, password, "7");
            await MvccProcess.Run("checkpoint", Filename, password);
            File.ReadAllBytes(path).Should().Equal(bytes);
            File.ReadAllBytes(Filename + "-shared-live").Should().Equal(live);
            File.Exists(Filename + "-shared-disabled").Should().BeTrue();
            VerifyCold(password, 7);
        }

        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Cold_database_replacement_never_reuses_the_saved_authority(string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            byte[] oldLive, oldPage;
            using (var page = SharedCoordinationPage.Open(Filename)) page.Opened(999);
            oldLive = File.ReadAllBytes(Filename + "-shared-live");
            oldPage = File.ReadAllBytes(Filename + "-shared-state");
            var replacement = Path.Combine(_directory, "replacement.db");
            await MvccProcess.Run("seed", replacement, password);
            await MvccProcess.Run("write", replacement, password, "7");
            await MvccProcess.Run("checkpoint", replacement, password);
            foreach (var file in Directory.GetFiles(_directory, "test*.db")) File.Delete(file);
            File.Move(replacement, Filename);
            // Replay a coherent saved pair at the same path after every handle is gone.
            File.WriteAllBytes(Filename + "-shared-live", oldLive);
            File.WriteAllBytes(Filename + "-shared-state", oldPage);
            using (var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password }))
            using (var database = new LiteDatabase(engine))
            {
                for (var i = 0; i < 3; i++) database.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(7);
                Authority(engine).TryRead(out var status).Should().BeTrue();
                status.Version.Should().NotBe(999);
                File.ReadAllBytes(Filename + "-shared-live").Skip(48).Take(16)
                    .Should().NotEqual(oldLive.Skip(48).Take(16));
            }
            VerifyCold(password, 7);
        }

        private void SeedLegacy()
        {
            SharedCoordinationPage.TryRetire(Filename);
            File.WriteAllBytes(Filename + "-shared-live", BitConverter.GetBytes(SharedCoordinationProtocol.LegacyMagic));
            var page = new byte[4096];
            SharedCoordinationProtocol.Write(page, 0, SharedCoordinationProtocol.LegacyMagic);
            SharedCoordinationProtocol.Write(page, 16, 123);
            SharedCoordinationProtocol.Write(page, 48, 1);
            File.WriteAllBytes(Filename + "-shared-state", page);
            File.WriteAllBytes(Filename + "-shared-disabled", BitConverter.GetBytes(SharedCoordinationProtocol.LegacyMagic));
        }

        private static SharedCoordinationPage Authority(SharedEngine engine) =>
            (SharedCoordinationPage)typeof(SharedEngine).GetField("_coordination", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);

        private void VerifyMapped(string password, int value)
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password });
            using var database = new LiteDatabase(engine);
            for (var i = 0; i < 3; i++) database.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(value);
            Authority(engine).Should().NotBeNull();
            Authority(engine).TryRead(out _).Should().BeTrue();
        }

        private void VerifyCold(string password, int revision)
        {
            using var database = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password });
            foreach (var name in new[] { "docs", "cold" })
            {
                var collection = database.GetCollection(name);
                var rows = collection.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                rows.Length.Should().Be(64);
                for (var id = 0; id < rows.Length; id++)
                {
                    rows[id]["_id"].AsInt32.Should().Be(id);
                    rows[id]["value"].AsInt32.Should().Be(name == "docs" ? revision : 0);
                    rows[id]["payload"].AsString.Should().Be(new string('x', 3000));
                    collection.FindById(id).ToString().Should().Be(rows[id].ToString());
                }
            }
            _passed = true;
        }

        public void Dispose()
        {
            if (_passed) Directory.Delete(_directory, true);
            else Console.Error.WriteLine("Preserved failed ABI-process database: " + _directory);
        }
    }
}
#endif
