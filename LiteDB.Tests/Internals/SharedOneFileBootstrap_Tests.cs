#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    public class SharedOneFileBootstrap_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-bootstrap-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedOneFileBootstrap_Tests()
        {
            Directory.CreateDirectory(_directory);
            using var db = new LiteDatabase(Filename);
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 7, ["payload"] = new string('z', 20000) });
        }
        internal static readonly string[] Cuts = { "created", "invalidated", "allocated", "mapped", "odd", "envelope-a", "envelope-b",
            "before-storage", "after-storage", "before-ready", "ready", "before-dms-downgrade", "downgraded", "write-released", "claimed", "before-close", "closed" };
        public static IEnumerable<object[]> Boundaries => Cuts.Select(s => new object[] { s });
        private EngineSettings Settings(int processes = 256, int generations = 256) => new EngineSettings
        { Filename = Filename, SharedMode = true, SharedProcessCapacity = processes, SharedGenerationCapacity = generations };
        private SharedMemoryAuthority Open(EngineSettings settings, NativeAdmission admission, Action validate = null) =>
            SharedMemoryAuthority.Open(settings, admission.Identity, validate ?? (() => Validate(settings, admission)));
        internal static void Validate(EngineSettings settings, NativeAdmission admission)
        {
            using var mutex = SharedMutexFactory.Create(SharedMutexNameFactory.Create(settings.Filename, settings.SharedMutexNameStrategy));
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
            try
            {
                var coreSettings = settings.Clone();
                coreSettings.SharedAdmission = admission;
                using var core = new LiteEngine(coreSettings);
                using var db = new LiteDatabase(core, disposeOnClose: false);
                db.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("acknowledged");
                db.GetCollection("docs").FindOne(Query.EQ("value", "acknowledged"))["_id"].AsInt32.Should().Be(1);
                db.GetCollection("unrelated").FindById(7)["payload"].AsString.Should().Be(new string('z', 20000));
            }
            finally { mutex.ReleaseMutex(); }
        }

        [OneFileTheory]
        [MemberData(nameof(Boundaries))]
        public void Every_bootstrap_exception_boundary_releases_and_reinitializes_twice(string cut)
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var hit = false;
                SharedMemoryAuthority.BootstrapStage = stage => { if (stage == cut) { hit = true; throw new IOException("injected " + cut); } };
                try
                {
                    Action fail = () => { using var authority = Open(settings, admission); };
                    fail.Should().Throw<IOException>().WithMessage("injected " + cut);
                    hit.Should().BeTrue();
                }
                finally { SharedMemoryAuthority.BootstrapStage = null; }
            }
            using var recovered = Open(settings, admission);
            recovered.Map.Load(SharedMemoryLayout.Control).Should().Be(1);
            Directory.GetFiles(_directory).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "test.db", "test.db-shm" });
        }

        [OneFileTheory]
        [MemberData(nameof(Boundaries))]
        public async Task Every_bootstrap_process_death_boundary_recovers_after_repeated_death(string cut)
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var child = new MvccProcess("one-file-bootstrap-cut", Filename, null, cut);
                await child.Expect("ready");
                await child.Kill();
            }
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            using var recovered = Open(settings, admission);
            recovered.Map.Load(SharedMemoryLayout.Control).Should().Be(1);
        }

        [OneFileFact]
        public void Wake_name_follows_envelope_publication_and_quiescent_replacement_retires_the_old_name()
        {
            var settings = Settings(2, 3);
            using var admission = NativeAdmission.Open(settings, true);
            var identities = new HashSet<Guid>();
            SharedMemoryAuthority.BootstrapStage = stage =>
            {
                if (stage != "envelope-b" && stage != "before-storage") return;
                var bytes = File.ReadAllBytes(SharedMemoryAuthority.PathFor(Filename));
                var first = SharedMemoryEnvelope.Decode(bytes.Take(SharedMemoryLayout.EnvelopeBytes).ToArray(),
                    Filename, admission.Identity, quiescent: true);
                var second = SharedMemoryEnvelope.Decode(bytes.Skip(SharedMemoryLayout.Page)
                    .Take(SharedMemoryLayout.EnvelopeBytes).ToArray(), Filename, admission.Identity, quiescent: true);
                second.Authority.Should().Be(first.Authority);
                identities.Add(first.Authority);
                SharedPlatformWake_Tests.AssertDarwinNameExists(first.Authority, stage == "before-storage");
            };
            try
            {
                Guid old;
                using (var first = Open(settings, admission)) old = first.Header.Authority;
                // Raw authority disposal closes handles but retains the published
                // namespace for recovery; a quiescent capacity reset must retire it.
                SharedPlatformWake_Tests.AssertDarwinNameExists(old, true);
                using (var replacement = Open(Settings(3, 5), admission))
                {
                    replacement.Header.Authority.Should().NotBe(old);
                    SharedPlatformWake_Tests.AssertDarwinNameExists(old, false);
                }
                identities.Count.Should().Be(2);
                for (var repeat = 0; repeat < 2; repeat++) Validate(settings, admission);
            }
            finally
            {
                SharedMemoryAuthority.BootstrapStage = null;
                foreach (var identity in identities) SharedWakeSignal.Retire(identity);
            }
        }

        [OneFileFact]
        public void First_opener_chooses_capacity_later_openers_map_declared_size_and_only_quiescence_changes_it()
        {
            var settings = Settings(2, 3);
            settings.SharedMemorySize = 65536;
            using var admission = NativeAdmission.Open(settings, true);
            Guid old;
            using (var first = Open(settings, admission))
            {
                old = first.Header.Authority;
                using var second = Open(Settings(8, 1024), admission);
                second.Header.Authority.Should().Be(old);
                second.Header.Processes.Should().Be(2);
                second.Header.Generations.Should().Be(3);
                second.Map.Size.Should().Be(65536);
                new FileInfo(SharedMemoryAuthority.PathFor(Filename)).Length.Should().Be(65536);
                Action full = () => { using var third = Open(settings, admission); };
                full.Should().Throw<IOException>().WithMessage("*capacity*");
            }
            using var resized = Open(Settings(3, 512), admission);
            resized.Header.Authority.Should().NotBe(old);
            resized.Header.Processes.Should().Be(3);
            resized.Header.Generations.Should().Be(512);
        }

        [OneFileTheory]
        [InlineData(8)] [InlineData(48)] [InlineData(80)] [InlineData(112)] [InlineData(224)]
        public void Unknown_abi_capabilities_identity_and_damaged_live_headers_refuse_without_mutation(int offset)
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            using var live = Open(settings, admission);
            var original = live.Map.Read(0, 256);
            var changed = (byte[])original.Clone();
            changed[offset] ^= 4;
            live.Map.Write(0, changed);
            var before = File.ReadAllBytes(Filename);
            Action attach = () => { using var other = Open(settings, admission); };
            attach.Should().Throw<IOException>();
            File.ReadAllBytes(Filename).Should().Equal(before);
            live.Map.Read(0, 256).Should().Equal(changed);
            live.Map.Write(0, original);
            using var valid = Open(settings, admission);
            valid.Header.Authority.Should().Be(live.Header.Authority);
        }

        [OneFileFact]
        public void Replay_or_copied_path_cannot_authorize_another_database()
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            byte[] saved;
            using (var authority = Open(settings, admission)) saved = File.ReadAllBytes(Filename + "-shm");
            var other = Path.Combine(_directory, "other.db");
            File.Copy(Filename, other);
            File.WriteAllBytes(other + "-shm", saved);
            var copied = new EngineSettings { Filename = other, SharedMode = true };
            using var otherAdmission = NativeAdmission.Open(copied, true);
            var validated = false;
            Action attach = () => { using var authority = SharedMemoryAuthority.Open(copied, otherAdmission.Identity, () => validated = true); };
            attach.Should().Throw<IOException>().WithMessage("*identity/namespace*");
            validated.Should().BeFalse();
            File.ReadAllBytes(other + "-shm").Should().Equal(saved);
            // Same-path replay is always discarded under exclusive DMS, then independently validated.
            var calls = 0;
            using var reset = Open(settings, admission, () => { calls++; Validate(settings, admission); });
            calls.Should().Be(1);
        }

        [OneFileFact]
        public void Allocation_and_mapping_failures_preserve_storage_and_release_bootstrap()
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            var before = File.ReadAllBytes(Filename);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                SharedMemoryAuthority.AllocateFile = (file, size) => 28; // modeled ENOSPC
                try
                {
                    Action full = () => { using var authority = Open(settings, admission); };
                    full.Should().Throw<IOException>().WithMessage("*allocation failed*");
                }
                finally { SharedMemoryAuthority.AllocateFile = null; }
                SharedMemoryAuthority.MapFile = (file, size) => throw new IOException("mapping failed");
                try
                {
                    Action map = () => { using var authority = Open(settings, admission); };
                    map.Should().Throw<IOException>().WithMessage("mapping failed");
                }
                finally { SharedMemoryAuthority.MapFile = null; }
            }
            File.ReadAllBytes(Filename).Should().Equal(before);
            using var recovered = Open(settings, admission);
        }

        [OneFileFact]
        public void Torn_ephemeral_checksums_reset_only_without_surviving_participants()
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            using (var authority = Open(settings, admission))
            {
                foreach (var offset in new[] { 0, SharedMemoryLayout.Page })
                {
                    var bytes = authority.Map.Read(offset, 256);
                    Array.Clear(bytes, 224, 32); // torn trailing checksum stores
                    authority.Map.Write(offset, bytes);
                }
                Action attach = () => { using var other = Open(settings, admission); };
                attach.Should().Throw<IOException>().WithMessage("*checksum*");
            }
            for (var repeat = 0; repeat < 2; repeat++) { using var reset = Open(settings, admission); }
        }

        [OneFileFact]
        public void Successful_bootstrap_visits_all_registered_boundaries_and_invalid_settings_execute_nothing()
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            var hits = new List<string>();
            SharedMemoryAuthority.BootstrapStage = hits.Add;
            try { using var authority = Open(settings, admission); }
            finally { SharedMemoryAuthority.BootstrapStage = null; }
            hits.Should().Equal(Cuts);
            settings.SharedMemorySize = 4096;
            var executed = false;
            Action invalid = () => { using var authority = Open(settings, admission, () => executed = true); };
            invalid.Should().Throw<ArgumentOutOfRangeException>();
            executed.Should().BeFalse();
            using var again = Open(Settings(), admission);
        }

        [OneFileFact]
        public void Empty_and_zero_partial_initialization_reset_but_declared_wrong_length_refuses()
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            File.WriteAllBytes(Filename + "-shm", new byte[7]);
            using (var recovered = Open(settings, admission)) recovered.Header.Size.Should().Be(1064960);
            using (var raw = new FileStream(Filename + "-shm", FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                raw.SetLength(8192);
            var executed = false;
            Action truncated = () => { using var authority = Open(settings, admission, () => executed = true); };
            truncated.Should().Throw<IOException>().WithMessage("*length*");
            executed.Should().BeFalse();
        }

        [OneFileFact]
        public void Live_authority_refuses_a_different_main_mutex_namespace_before_storage_validation()
        {
            var settings = Settings();
            using var admission = NativeAdmission.Open(settings, true);
            using var first = Open(settings, admission);
            var changed = Settings();
            changed.SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash;
            var executed = false;
            Action attach = () => { using var other = Open(changed, admission, () => executed = true); };
            attach.Should().Throw<IOException>().WithMessage("*namespace*");
            executed.Should().BeFalse();
        }

        [OneFileFact]
        public void Connection_string_round_trips_configurable_size_and_capacity()
        {
            var connection = new ConnectionString("filename=a.db;connection=shared;shared one file=true;shared memory size=2MB;shared process capacity=8;shared generation capacity=512");
            var copy = new ConnectionString(connection.ToStringWithPassword());
            copy.SharedOneFile.Should().BeTrue();
            copy.SharedMemorySize.Should().Be(2 * 1024 * 1024);
            copy.SharedProcessCapacity.Should().Be(8);
            copy.SharedGenerationCapacity.Should().Be(512);
            var defaults = SharedMemoryEnvelope.Create(Settings(), "identity");
            defaults.Size.Should().Be(1064960);
        }
        public void Dispose() { SharedMemoryAuthority.BootstrapStage = null; Directory.Delete(_directory, true); }
    }
}
#endif
