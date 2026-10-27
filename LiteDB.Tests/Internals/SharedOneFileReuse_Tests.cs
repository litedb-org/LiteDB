#if NET8_0_OR_GREATER
using System;
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
    public class SharedOneFileReuse_Tests : IDisposable
    {
        private readonly MappedTestFile _file = new MappedTestFile();
        private string Filename => _file.Filename;
        public SharedOneFileReuse_Tests()
        {
            using var db = new LiteDatabase(Filename);
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "acknowledged" });
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("unrelated").Insert(new BsonDocument { ["_id"] = 7, ["payload"] = new string('z', 20000) });
        }
        private EngineSettings Settings() => new EngineSettings { Filename = Filename, SharedMode = true };
        private static SharedMemoryAuthority Open(EngineSettings settings, NativeAdmission admission) =>
            SharedMemoryAuthority.Open(settings, admission.Identity, () => SharedOneFileBootstrap_Tests.Validate(settings, admission));

        [OneFileTheory]
        [InlineData("matching")]
        [InlineData("checksum")]
        [InlineData("identity")]
        [InlineData("layout")]
        [InlineData("process-capacity")]
        [InlineData("generation-capacity")]
        [InlineData("size")]
        public void Quiescent_reuse_or_reset_is_discriminated_and_the_result_reuses_again(string mismatch)
        {
            var settings = Settings();
            using var native = NativeAdmission.Open(settings, true);
            Guid original;
            using (var map = Open(settings, native)) original = map.Header.Authority;
            if (mismatch == "checksum" || mismatch == "identity" || mismatch == "layout")
            {
                var bytes = File.ReadAllBytes(Filename + "-shm");
                if (mismatch == "layout")
                {
                    // A supported alternate stride/layout, rather than an unknown ABI.
                    var alternate = Settings();
                    alternate.SharedGenerationCapacity = 1024;
                    var envelope = SharedMemoryEnvelope.Create(alternate, native.Identity);
                    Array.Resize(ref bytes, (int)envelope.Size);
                    WriteEnvelopes(bytes, envelope.Bytes);
                }
                else
                {
                    var header = bytes.Take(256).ToArray();
                    header[mismatch == "checksum" ? 224 : 80] ^= 4;
                    if (mismatch == "identity") Rehash(header);
                    WriteEnvelopes(bytes, header);
                }
                File.WriteAllBytes(Filename + "-shm", bytes);
            }
            if (mismatch == "process-capacity") settings.SharedProcessCapacity++;
            if (mismatch == "generation-capacity") settings.SharedGenerationCapacity++;
            if (mismatch == "size") settings.SharedMemorySize = 2 * 1024 * 1024;
            var reuse = SharedMemoryAuthority.BootstrapReuseCount;
            var reset = SharedMemoryAuthority.BootstrapResetCount;
            Guid current;
            using (var map = Open(settings, native))
            {
                current = map.Header.Authority;
                if (mismatch == "matching") current.Should().Be(original);
                else current.Should().NotBe(original);
                map.Header.Size.Should().Be(SharedMemoryEnvelope.Create(settings, native.Identity).Size);
                map.Map.Load(SharedMemoryLayout.Control).Should().Be(1);
            }
            SharedMemoryAuthority.BootstrapReuseCount.Should().Be(reuse + (mismatch == "matching" ? 1 : 0));
            SharedMemoryAuthority.BootstrapResetCount.Should().Be(reset + (mismatch == "matching" ? 0 : 1));
            using var again = Open(settings, native);
            again.Header.Authority.Should().Be(current);
            SharedMemoryAuthority.BootstrapReuseCount.Should().Be(reuse + (mismatch == "matching" ? 2 : 1));
        }

        [OneFileTheory]
        [InlineData("namespace", "*identity/namespace*")]
        [InlineData("unknown-layout", "*ABI/layout*")]
        [InlineData("physical-size", "*length*")]
        public void Untrusted_namespace_unknown_layout_and_wrong_physical_size_refuse_without_reset(string mismatch, string message)
        {
            var settings = Settings();
            using var native = NativeAdmission.Open(settings, true);
            using (var map = Open(settings, native)) { }
            var pristine = File.ReadAllBytes(Filename + "-shm");
            var changed = (byte[])pristine.Clone();
            if (mismatch == "physical-size") Array.Resize(ref changed, 8192);
            else
            {
                var header = changed.Take(256).ToArray();
                header[mismatch == "namespace" ? 112 : 8] ^= 4;
                Rehash(header);
                WriteEnvelopes(changed, header);
            }
            File.WriteAllBytes(Filename + "-shm", changed);
            var data = File.ReadAllBytes(Filename);
            var resets = SharedMemoryAuthority.BootstrapResetCount;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var validated = false;
                Action fail = () => { using var map = SharedMemoryAuthority.Open(settings, native.Identity, () => validated = true); };
                fail.Should().Throw<IOException>().WithMessage(message);
                validated.Should().BeFalse();
                File.ReadAllBytes(Filename + "-shm").Should().Equal(changed);
                File.ReadAllBytes(Filename).Should().Equal(data);
                SharedMemoryAuthority.BootstrapResetCount.Should().Be(resets);
            }
            File.WriteAllBytes(Filename + "-shm", pristine);
            using var recovered = Open(settings, native);
        }

        [OneFileTheory]
        [InlineData("before-storage")]
        [InlineData("validating")]
        [InlineData("startup-before-tail-trim")]
        [InlineData("startup-after-tail-trim")]
        [InlineData("after-storage")]
        [InlineData("before-ready")]
        [InlineData("ready")]
        [InlineData("downgraded")]
        [InlineData("claimed")]
        [InlineData("before-close")]
        [InlineData("closed")]
        public async Task Death_during_proven_reuse_validation_repeats_and_recovers_the_same_authority(string cut)
        {
            var settings = Settings();
            Guid authority;
            using (var admission = NativeAdmission.Open(settings, true))
            using (var map = Open(settings, admission)) authority = map.Header.Authority;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                if (cut.StartsWith("startup-", StringComparison.Ordinal))
                    using (var tail = new FileStream(Filename, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        tail.WriteByte(42);
                using var child = new MvccProcess("one-file-reuse-cut", Filename, null, cut);
                await child.Expect("ready");
                await child.Kill();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var admission = NativeAdmission.Open(settings, true);
                using var map = Open(settings, admission);
                map.Header.Authority.Should().Be(authority);
                map.Map.Load(SharedMemoryLayout.Control).Should().Be(1);
                (map.Map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(0);
            }
        }

        [OneFileTheory]
        [InlineData("process-capacity")]
        [InlineData("generation-capacity")]
        [InlineData("size")]
        public async Task Remote_participant_prevents_capacity_change_until_its_process_exits(string mismatch)
        {
            var settings = Settings();
            settings.SharedProcessCapacity = 1;
            settings.SharedGenerationCapacity = 3;
            using var native = NativeAdmission.Open(settings, true);
            using (var child = new MvccProcess("one-file-authority-hold", Filename, null))
            {
                await child.Expect("ready");
                var before = File.ReadAllBytes(Filename + "-shm");
                if (mismatch == "process-capacity") settings.SharedProcessCapacity = 2;
                if (mismatch == "generation-capacity") settings.SharedGenerationCapacity = 512;
                if (mismatch == "size") settings.SharedMemorySize = 2 * 1024 * 1024;
                var validated = false;
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    Action attach = () => { using var map = SharedMemoryAuthority.Open(settings, native.Identity, () => validated = true); };
                    attach.Should().Throw<IOException>().WithMessage("*capacity*");
                    validated.Should().BeFalse();
                    File.ReadAllBytes(Filename + "-shm").Should().Equal(before);
                }
                await child.Kill(sharedFilesRemainOpen: true);
            }
            using var resized = Open(settings, native);
            resized.Header.Size.Should().Be(SharedMemoryEnvelope.Create(settings, native.Identity).Size);
            resized.Header.Processes.Should().Be(settings.SharedProcessCapacity);
            resized.Header.Generations.Should().Be(settings.SharedGenerationCapacity);
        }

        [OneFileFact]
        public async Task Remote_participant_with_a_free_slot_keeps_declared_layout_despite_larger_requests()
        {
            var settings = Settings();
            settings.SharedProcessCapacity = 3;
            settings.SharedGenerationCapacity = 512;
            settings.SharedMemorySize = 2 * 1024 * 1024;
            using var native = NativeAdmission.Open(settings, true);
            using (var child = new MvccProcess("one-file-authority-hold", Filename, null, "2"))
            {
                await child.Expect("ready");
                var envelope = File.ReadAllBytes(Filename + "-shm").Take(256).ToArray();
                using (var existing = Open(settings, native))
                {
                    existing.Header.Processes.Should().Be(2);
                    existing.Header.Generations.Should().Be(3);
                    existing.Header.Bytes.Should().Equal(envelope);
                    new FileInfo(Filename + "-shm").Length.Should().Be(existing.Header.Size);
                }
                child.Send("close");
                await child.Expect("done");
                await child.Finish();
            }
            using var resized = Open(settings, native);
            resized.Header.Processes.Should().Be(3);
            resized.Header.Generations.Should().Be(512);
            resized.Header.Size.Should().Be(2 * 1024 * 1024);
        }

        private static void WriteEnvelopes(byte[] target, byte[] header)
        {
            Buffer.BlockCopy(header, 0, target, 0, 256);
            Buffer.BlockCopy(header, 0, target, SharedMemoryLayout.Page, 256);
        }
        private static void Rehash(byte[] header) => Buffer.BlockCopy(
            SharedMemoryEnvelope.Hash(header.Take(224).ToArray()), 0, header, 224, 32);
        public void Dispose() => _file.Dispose();
    }
}
#endif
