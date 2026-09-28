#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class NativeAdmissionHandoffProof_Tests
    {
        public static IEnumerable<object[]> Boundaries()
        {
            foreach (var stage in new[] { "before-recovery-marker", "after-source-backup", "after-temp-install", "before-recovery-marker-delete" })
            foreach (var shared in new[] { false, true })
            foreach (var password in new[] { null, "secret" })
                yield return new object[] { stage, shared, password };
        }

        [Theory]
        [MemberData(nameof(Boundaries))]
        public async Task Both_inodes_are_locked_through_publication_and_only_the_live_inode_remains_owned(
            string stage, bool shared, string password)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-handoff-proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "data.db");
            try
            {
                NativeAdmission_Tests.Seed(filename, password);
                var sourceId = Identity(filename);
                using var child = new MvccProcess("native-rebuild-hold", filename, password,
                    stage + (shared ? "|shared" : "|direct"));
                await child.Expect("ready"); // The actual installation boundary was reached.
                var source = stage == "before-recovery-marker" ? filename : FileHelper.GetSuffixFile(filename, "-backup", false);
                var candidate = stage == "before-recovery-marker" || stage == "after-source-backup"
                    ? FileHelper.GetSuffixFile(filename, "-temp", false) : filename;
                Identity(source).Should().Be(sourceId);
                var replacementId = Identity(candidate);
                replacementId.Should().NotBe(sourceId, "the test must observe a real inode handoff");
                AssertLocked(source, true);
                AssertLocked(candidate, true);
                child.Send("continue");
                await child.Expect("installed");
                Identity(filename).Should().Be(replacementId);
                AssertLocked(filename, true);
                AssertLocked(FileHelper.GetSuffixFile(filename, "-backup", false), false);
                await child.Finish(release: true);
                AssertLocked(filename, false);
                NativeAdmission_Tests.Verify(filename, password);
            }
            finally { Directory.Delete(directory, true); }
        }

        private static string Identity(string filename)
        {
            using var probe = new DatabaseFileLock(filename, readOnly: true, create: false);
            return probe.Identity;
        }

        private static void AssertLocked(string filename, bool expected)
        {
            var bytes = TempFile.ReadAllBytesShared(filename);
            using (var probe = new DatabaseFileLock(filename, readOnly: true, create: false))
                probe.Conflicts(DatabaseFileLock.Admission).Should().Be(expected,
                    "a recovery marker or registry refusal cannot substitute for the native lock on " + filename);
            TempFile.ReadAllBytesShared(filename).Should().Equal(bytes);
        }
    }
}
#endif
