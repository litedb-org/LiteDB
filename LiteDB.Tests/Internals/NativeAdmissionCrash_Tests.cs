#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class NativeAdmissionCrash_Tests
    {
        [Theory]
        [InlineData("before-recovery-marker", null)]
        [InlineData("before-recovery-marker-flush", null)]
        [InlineData("after-log-backup", null)]
        [InlineData("after-source-backup", null)]
        [InlineData("after-temp-install", null)]
        [InlineData("before-recovery-marker-delete", null)]
        [InlineData("before-recovery-marker", "secret")]
        [InlineData("before-recovery-marker-flush", "secret")]
        [InlineData("after-log-backup", "secret")]
        [InlineData("after-source-backup", "secret")]
        [InlineData("after-temp-install", "secret")]
        [InlineData("before-recovery-marker-delete", "secret")]
        public async Task Death_during_handoff_releases_native_locks_and_preserves_recoverable_data(string stage, string password)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-native-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "data.db");
            try
            {
                NativeAdmission_Tests.Seed(filename, password);
                using (var child = new MvccProcess("native-rebuild-hold", filename, password, stage))
                {
                    await child.Expect("ready");
                    await child.Kill();
                }
                var marker = RebuildRecovery.GetMarkerFilename(filename);
                if (stage == "before-recovery-marker")
                {
                    File.Exists(marker).Should().BeFalse();
                    NativeAdmission_Tests.Verify(filename, password);
                    return;
                }
                var files = Directory.GetFiles(directory);
                var bytes = Array.ConvertAll(files, File.ReadAllBytes);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    foreach (var connection in new[] { ConnectionType.Direct, ConnectionType.Shared })
                    {
                        Action open = () =>
                        {
                            using var db = new LiteDatabase(new ConnectionString
                                { Filename = filename, Password = password, Connection = connection });
                            db.GetCollection("rows").Count();
                        };
                        open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    }
                }
                Directory.GetFiles(directory).Should().BeEquivalentTo(files);
                for (var i = 0; i < files.Length; i++) File.ReadAllBytes(files[i]).Should().Equal(bytes[i]);
                var candidate = stage == "after-temp-install" || stage == "before-recovery-marker-delete"
                    ? filename : FileHelper.GetSuffixFile(filename, "-temp", false);
                var recovered = Path.Combine(directory, "recovered.db");
                File.Copy(candidate, recovered);
                NativeAdmission_Tests.Verify(recovered, password);
                NativeAdmission_Tests.Verify(recovered, password);
                // Recovery at a separate path needs no stale-admission cleanup.
                Directory.GetFiles(directory, "*-shared-mode").Should().BeEmpty();
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
