#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileRebuildGuardInput_Tests
    {
        [OneFileTheory]
        [InlineData(null, "after-temp-install")]
        [InlineData("secret", "after-temp-install")]
        [InlineData(null, "before-temp-install")]
        [InlineData("secret", "before-temp-install")]
        public async Task Parent_directory_symlink_recovers_the_canonical_guard(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            var directory = Path.GetDirectoryName(files.Filename);
            var alias = directory + "-alias";
            Directory.CreateSymbolicLink(alias, directory);
            try
            {
                var settings = files.Settings;
                settings.Filename = Path.Combine(alias, Path.GetFileName(files.Filename));
                using (var engine = new SharedEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false)) Verify(db, 0);
                await Interrupt(files, cut);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    using var engine = new SharedEngine(settings);
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    Verify(db, 11);
                }
                SharedOneFileMaintenanceCrash_Tests.Recover(files);
            }
            finally { Directory.Delete(alias); }
        }

        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Moved_bundle_refuses_without_rewriting_foreign_absolute_names(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            await Interrupt(files);
            var original = Path.GetDirectoryName(files.Filename);
            var moved = original + "-moved";
            Directory.Move(original, moved);
            try
            {
                var settings = files.Settings;
                settings.Filename = Path.Combine(moved, Path.GetFileName(files.Filename));
                RefuseWithoutMutation(settings);
            }
            finally { Directory.Move(moved, original); }
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }

        [OneFileTheory]
        [InlineData("foreign-magic")]
        [InlineData("foreign-file")]
        [InlineData("relative-backup")]
        [InlineData("outside-backup")]
        [InlineData("duplicate-path")]
        [InlineData("log-as-candidate")]
        [InlineData("foreign-inode")]
        [InlineData("trailing-payload")]
        [InlineData("invalid-string")]
        [InlineData("invalid-path")]
        [InlineData("oversized")]
        public async Task Valid_checksum_does_not_authorize_malformed_or_foreign_recovery(string kind)
        {
            using var files = new SharedOneFilePublicationFixture();
            await Interrupt(files);
            var marker = RebuildRecovery.GetMarkerFilename(files.Filename);
            var valid = File.ReadAllBytes(marker);
            var fields = RebuildGuardRecord.Decode(valid);
            switch (kind)
            {
                case "foreign-magic": fields[0] = "foreign protocol"; break;
                case "foreign-file": fields[1] += ".foreign"; break;
                case "relative-backup": fields[2] = Path.GetFileName(fields[2]); break;
                case "outside-backup": fields[2] = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(files.Filename)), "foreign.db"); break;
                case "duplicate-path": fields[3] = fields[2]; break;
                case "log-as-candidate": fields[4] = files.Log; break;
                case "invalid-path": fields[2] = "invalid\0path"; break;
                case "foreign-inode": fields[5] = "unrelated-identity"; break;
            }
            var corrupt = RebuildGuardRecord.Encode(fields);
            if (kind == "trailing-payload") corrupt = RebuildGuardRecord.Encode(fields.Concat(new[] { "extra" }).ToArray());
            if (kind == "invalid-string")
            {
                // A valid checksum around an invalid BinaryReader string length.
                corrupt = new byte[] { 0xff, 0xff, 0xff, 0xff, 0xff };
                using var sha = System.Security.Cryptography.SHA256.Create();
                corrupt = corrupt.Concat(sha.ComputeHash(corrupt)).ToArray();
            }
            if (kind == "oversized") corrupt = new byte[65537];
            File.WriteAllBytes(marker, corrupt);
            RefuseWithoutMutation(files.Settings, identityMismatch: kind == "foreign-inode");
            File.WriteAllBytes(marker, valid);
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }

        [OneFileTheory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)]
        [InlineData(3)] [InlineData(4)] [InlineData(5)]
        public async Task Hard_link_substitution_of_each_participating_inode_is_rejected_before_mutation(int field)
        {
            using var files = new SharedOneFilePublicationFixture();
            await Interrupt(files, field == 0 || field == 5 ? "before-log-backup" :
                field == 4 ? "before-temp-install" : "after-temp-install");
            var fields = RebuildGuardRecord.Decode(File.ReadAllBytes(RebuildRecovery.GetMarkerFilename(files.Filename)));
            // Exercise the candidate before publication as well as the live
            // replacement and the saved original data/WAL after publication.
            var path = field == 5 ? files.Log : fields[field == 0 ? 1 : field];
            var alias = path + ".hardlink";
            link(path, alias).Should().Be(0);
            try { RefuseWithoutMutation(files.Settings, identityMismatch: true); }
            finally { File.Delete(alias); }
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }

        [OneFileTheory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)]
        [InlineData(3)] [InlineData(4)] [InlineData(5)]
        public async Task A_new_inode_at_an_expected_name_cannot_replace_recovery_evidence(int field)
        {
            using var files = new SharedOneFilePublicationFixture();
            await Interrupt(files, field == 0 || field == 5 ? "before-log-backup" :
                field == 4 ? "before-temp-install" : "after-temp-install");
            var fields = RebuildGuardRecord.Decode(File.ReadAllBytes(RebuildRecovery.GetMarkerFilename(files.Filename)));
            var path = field == 5 ? files.Log : fields[field == 0 ? 1 : field];
            var saved = path + ".saved";
            File.Move(path, saved);
            File.Copy(saved, path); // identical bytes, foreign physical identity
            try { RefuseWithoutMutation(files.Settings, identityMismatch: true); }
            finally { File.Delete(path); File.Move(saved, path); }
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }

        [OneFileTheory]
        [InlineData(null, false)] [InlineData("secret", false)]
        [InlineData(null, true)] [InlineData("secret", true)]
        public async Task Read_only_storage_and_explicit_upgrade_do_not_authorize_guard_recovery(string password, bool upgrade)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            await Interrupt(files);
            var settings = files.Settings;
            settings.ReadOnlyStorage = true;
            settings.Upgrade = upgrade;
            RefuseWithoutMutation(settings, readOnly: true);
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }

        internal static void RefuseWithoutMutation(EngineSettings settings, bool identityMismatch = false, bool readOnly = false)
        {
            var directory = Path.GetDirectoryName(settings.Filename);
            var shmPath = settings.Filename + "-shm";
            var shm = File.Exists(shmPath) ? File.ReadAllBytes(shmPath) : null;
            var before = Directory.GetFiles(directory).Where(p => !p.EndsWith("-shm"))
                .ToDictionary(p => p, File.ReadAllBytes);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var error = Record.Exception(() => RebuildRecovery.EnsureAvailable(settings));
                if (identityMismatch) error.Should().BeAssignableTo<IOException>();
                else
                {
                    error.Should().BeOfType<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    if (readOnly) error.Message.Should().Contain("writable open is required");
                }
                if (readOnly)
                {
                    File.Exists(shmPath).Should().Be(shm != null);
                    if (shm != null) File.ReadAllBytes(shmPath).SequenceEqual(shm).Should().BeTrue();
                }
                Directory.GetFiles(directory).Where(p => !p.EndsWith("-shm")).Should().BeEquivalentTo(before.Keys);
                foreach (var entry in before) File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value).Should().BeTrue("guard refusal must preserve {0}", entry.Key);
            }
        }

        internal static async Task Interrupt(SharedOneFilePublicationFixture files, string cut = "after-temp-install")
        {
            using var child = new MvccProcess("one-file-maintenance-cut", files.Filename, files.Settings.Password, "install:" + cut);
            await child.Expect("ready");
            await child.Kill();
        }
        [DllImport("libc", SetLastError = true)] private static extern int link(string original, string alias);
    }
}
#endif
