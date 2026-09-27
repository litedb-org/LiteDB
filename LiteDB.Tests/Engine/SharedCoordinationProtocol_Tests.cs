#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationProtocol_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-abi-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string Live => SharedCoordinationFallback.LivePath(Filename);
        private string Page => SharedCoordinationFallback.PagePath(Filename);
        private string Marker => SharedCoordinationFallback.DisabledPath(Filename);
        public SharedCoordinationProtocol_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void Case_alias_attaches_to_one_authority_on_case_insensitive_filesystems()
        {
            var alias = Path.Combine(_directory, "TEST.DB");
            using var first = SharedCoordinationPage.Open(Filename);
            first.Opened(7);
            if (!File.Exists(SharedCoordinationFallback.LivePath(alias))) return; // Case-sensitive volume.
            var original = File.ReadAllBytes(Live);
            using var second = SharedCoordinationPage.Open(alias);
            second.Opened(7);
            first.Committed(9);
            second.TryRead(out var status).Should().BeTrue();
            status.Version.Should().Be(9);
            File.ReadAllBytes(Live).Should().Equal(original);
        }

        [Fact]
        public void Binding_case_rules_match_supported_platform_namespaces()
        {
            var lower = SharedCoordinationProtocol.CreateParticipation(Filename).Skip(64).Take(32);
            var upper = SharedCoordinationProtocol.CreateParticipation(Path.Combine(_directory, "TEST.DB")).Skip(64).Take(32);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                lower.Should().Equal(upper);
            else lower.Should().NotEqual(upper);
        }

        [Fact]
        public void Supported_protocol_attaches_to_the_same_live_authority()
        {
            using var first = SharedCoordinationPage.Open(Filename);
            first.Opened(7);
            var before = File.ReadAllBytes(Live);
            using var second = SharedCoordinationPage.Open(Filename);
            second.TryRead(out _).Should().BeFalse("attachment alone cannot validate storage");
            second.Opened(7);
            first.Committed(9);
            second.TryRead(out var status).Should().BeTrue();
            status.Version.Should().Be(9);
            File.ReadAllBytes(Live).Should().Equal(before);
        }

        [Fact]
        public void Unknown_optional_nonsemantic_capabilities_allow_attachment()
        {
            Seed();
            Change(Live, 40, long.MinValue);
            Change(Page, 40, long.MinValue);
            using var holder = Hold();
            using var page = SharedCoordinationPage.Open(Filename);
            page.Opened(7);
            page.TryRead(out _).Should().BeTrue();
        }

        public static System.Collections.Generic.IEnumerable<object[]> InvalidHeaders()
        {
            foreach (var live in new[] { false, true })
                foreach (var field in new[] { "magic", "newer", "unknown-older", "header-size", "layout-size",
                    "required-capabilities", "reserved", "database", "authority", "zero-authority", "truncated" })
                    yield return new object[] { live, field };
        }

        [Theory]
        [MemberData(nameof(InvalidHeaders))]
        public void Unknown_or_mismatched_headers_preserve_the_entire_authority(bool live, string field)
        {
            Seed();
            var path = live ? Live : Page;
            var bytes = File.ReadAllBytes(path);
            switch (field)
            {
                case "magic": bytes[0] ^= 1; break;
                case "newer": SharedCoordinationProtocol.Write(bytes, 8, 2); break;
                case "unknown-older": SharedCoordinationProtocol.Write(bytes, 8, 0); break;
                case "header-size": SharedCoordinationProtocol.Write(bytes, 16, 120); break;
                case "layout-size": SharedCoordinationProtocol.Write(bytes, 24, bytes.Length + 8); break;
                case "required-capabilities": SharedCoordinationProtocol.Write(bytes, 32, 1); break;
                case "reserved": bytes[96] = 1; break;
                case "database": bytes[64] ^= 1; break;
                case "authority": bytes[48] ^= 1; break;
                case "zero-authority": Array.Clear(bytes, 48, 16); break;
                case "truncated": bytes = bytes.Take(bytes.Length - 1).ToArray(); break;
            }
            File.WriteAllBytes(path, bytes);
            File.WriteAllBytes(Marker, BitConverter.GetBytes(SharedCoordinationProtocol.Magic));
            var saved = new[] { Live, Page, Marker }.Select(File.ReadAllBytes).ToArray();
            SharedCoordinationPage.TryRetire(Filename);
            Action attach = () => SharedCoordinationPage.Open(Filename).Dispose();
            attach.Should().Throw<IOException>();
            var paths = new[] { Live, Page, Marker };
            for (var i = 0; i < paths.Length; i++) File.ReadAllBytes(paths[i]).Should().Equal(saved[i]);
        }

        [Fact]
        public void A_copied_pair_cannot_attach_to_another_database_path()
        {
            Seed();
            var other = Path.Combine(_directory, "other.db");
            File.Copy(Live, SharedCoordinationFallback.LivePath(other));
            File.Copy(Page, SharedCoordinationFallback.PagePath(other));
            Action attach = () => SharedCoordinationPage.Open(other).Dispose();
            attach.Should().Throw<IOException>().WithMessage("*identity*");
            File.ReadAllBytes(SharedCoordinationFallback.PagePath(other)).Should().Equal(File.ReadAllBytes(Page));
        }

        [Fact]
        public void A_stale_page_cannot_join_a_new_authority_at_the_same_path()
        {
            Seed();
            var stale = File.ReadAllBytes(Page);
            SharedCoordinationPage.TryRetire(Filename);
            Seed();
            var live = File.ReadAllBytes(Live);
            File.WriteAllBytes(Page, stale);
            Action attach = () => SharedCoordinationPage.Open(Filename).Dispose();
            attach.Should().Throw<IOException>().WithMessage("*identity*");
            File.ReadAllBytes(Live).Should().Equal(live);
            File.ReadAllBytes(Page).Should().Equal(stale);
        }

        [Fact]
        public void An_orphan_page_cannot_manufacture_a_participation_proof()
        {
            Seed();
            var saved = File.ReadAllBytes(Page);
            File.Delete(Live);
            Action attach = () => SharedCoordinationPage.Open(Filename).Dispose();
            attach.Should().Throw<IOException>().WithMessage("*Unpaired*");
            File.Exists(Live).Should().BeFalse();
            File.ReadAllBytes(Page).Should().Equal(saved);
        }

        [Fact]
        public void An_unknown_revocation_marker_prevents_partial_retirement()
        {
            Seed();
            File.WriteAllBytes(Marker, new byte[] { 1, 2, 3 });
            var saved = new[] { Live, Page, Marker }.Select(File.ReadAllBytes).ToArray();
            SharedCoordinationPage.TryRetire(Filename);
            using (var page = SharedCoordinationPage.Open(Filename))
            {
                page.Opened(7);
                page.TryRead(out _).Should().BeFalse();
            }
            var paths = new[] { Live, Page, Marker };
            for (var i = 0; i < paths.Length; i++)
            {
                var bytes = File.ReadAllBytes(paths[i]);
                // Opening may publish mutable epochs; the complete header and marker stay intact.
                bytes.Take(i == 1 ? 128 : bytes.Length).Should().Equal(saved[i].Take(i == 1 ? 128 : bytes.Length));
            }
        }

        [Fact]
        public void Recognized_legacy_protocol_retires_only_after_all_live_users_are_gone()
        {
            SeedLegacy();
            var original = File.ReadAllBytes(Page);
            using (var holder = Hold())
            {
                SharedCoordinationPage.TryRetire(Filename);
                Action attach = () => SharedCoordinationPage.Open(Filename).Dispose();
                attach.Should().Throw<IOException>().WithMessage("*protocol*");
                File.ReadAllBytes(Page).Should().Equal(original);
            }
            using (var page = SharedCoordinationPage.Open(Filename))
            {
                SharedCoordinationProtocol.Read(File.ReadAllBytes(Live), 8).Should().Be(1);
                page.TryRead(out _).Should().BeFalse();
                page.Opened(0);
                page.TryRead(out _).Should().BeTrue();
            }
            SharedCoordinationPage.TryRetire(Filename);
            File.Exists(Live).Should().BeFalse();
            File.Exists(Page).Should().BeFalse();
        }

        [Fact]
        public void Extensions_to_the_unversioned_prototype_are_not_recognized_for_retirement()
        {
            SeedLegacy();
            Change(Page, 64, 1);
            var saved = File.ReadAllBytes(Page);
            SharedCoordinationPage.TryRetire(Filename);
            Action attach = () => SharedCoordinationPage.Open(Filename).Dispose();
            attach.Should().Throw<IOException>();
            File.ReadAllBytes(Page).Should().Equal(saved);
            new FileInfo(Live).Length.Should().Be(8);
        }

        [Theory]
        [InlineData("-shared-state")]
        [InlineData("-shared-disabled")]
        [InlineData("-shared-live")]
        public void Failed_retirement_is_repeatable_without_partial_authority_admission(string suffix)
        {
            SeedLegacy();
            File.WriteAllBytes(Marker, BitConverter.GetBytes(SharedCoordinationProtocol.LegacyMagic));
            var observed = false;
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (path == Filename + suffix && stage == "retired")
                {
                    observed = true;
                    throw new IOException("Injected retirement failure");
                }
            };
            try { SharedCoordinationPage.TryRetire(Filename); }
            finally { SharedCoordinationFile.CreationStage = null; }
            observed.Should().BeTrue();
            using var page = SharedCoordinationPage.Open(Filename);
            page.TryRead(out _).Should().BeFalse();
            page.Opened(3);
            page.TryRead(out var status).Should().BeTrue();
            status.Version.Should().Be(3);
            File.Exists(Marker).Should().BeFalse();
        }

        private void Seed() { using var page = SharedCoordinationPage.Open(Filename); page.Opened(7); }
        private FileStream Hold() => new FileStream(Live, FileMode.Open, FileAccess.Read, FileShare.Read);
        private static void Change(string path, int offset, long value)
        {
            var bytes = File.ReadAllBytes(path);
            SharedCoordinationProtocol.Write(bytes, offset, value);
            File.WriteAllBytes(path, bytes);
        }
        private void SeedLegacy()
        {
            File.WriteAllBytes(Live, BitConverter.GetBytes(SharedCoordinationProtocol.LegacyMagic));
            var bytes = new byte[4096];
            SharedCoordinationProtocol.Write(bytes, 0, SharedCoordinationProtocol.LegacyMagic);
            SharedCoordinationProtocol.Write(bytes, 16, 7);
            SharedCoordinationProtocol.Write(bytes, 48, 1);
            File.WriteAllBytes(Page, bytes);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
