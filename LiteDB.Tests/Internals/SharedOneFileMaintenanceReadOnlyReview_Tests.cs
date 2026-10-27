#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenanceReadOnlyReview_Tests
    {
        [OneFileTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Read_only_open_must_preserve_an_interrupted_rebuild_bundle(string password)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            using (var install = new MvccProcess("one-file-maintenance-cut", files.Filename, password,
                "install:after-temp-install"))
            {
                await install.Expect("ready");
                await install.Kill();
            }

            var directory = Path.GetDirectoryName(files.Filename);
            var before = Directory.GetFiles(directory).Where(path => !path.EndsWith("-shm"))
                .ToDictionary(path => path, File.ReadAllBytes);
            before.Should().ContainKey(RebuildRecovery.GetMarkerFilename(files.Filename));
            var settings = files.Settings;
            settings.ReadOnly = true;

            using (new AssertionScope())
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    Action open = () => { using var engine = new SharedEngine(settings); };
                    var error = Record.Exception(open);
                    error.Should().BeOfType<LiteException>("restoring the guarded pair requires a writable recovery open");
                    if (error is LiteException lite) lite.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                    Directory.GetFiles(directory).Where(path => !path.EndsWith("-shm"))
                        .Should().BeEquivalentTo(before.Keys, "a read-only attempt must preserve recovery evidence");
                    foreach (var entry in before.Where(entry => File.Exists(entry.Key)))
                        File.ReadAllBytes(entry.Key).SequenceEqual(entry.Value).Should().BeTrue(
                            "read-only access must preserve {0}", Path.GetFileName(entry.Key));
                }
            }

            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }
    }
}
#endif
