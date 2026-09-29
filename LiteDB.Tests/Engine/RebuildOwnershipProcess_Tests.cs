#if !NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RebuildOwnershipProcess_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Actual_open_waits_during_handoff_after_physical_claims_are_closed(bool encrypted)
        {
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            using var owner = new MvccProcess("rebuild-ownership", file.Filename, password, "after-rebuild-data-close");
            await owner.Expect("ready");
            using var reader = new MvccProcess("rebuild-waiting-open", file.Filename, password);
            await reader.Expect("admitting");
            var opened = reader.ReadLine(TimeSpan.FromSeconds(20));
            var first = await Task.WhenAny(opened, Task.Delay(300));
            first.Should().NotBeSameAs(opened, "admission cannot finish before the recovery owner releases its claim");
            owner.Send("continue");
            await owner.Expect("done");
            await owner.Finish();
            (await opened).Should().Be("opened");
            await reader.Finish();
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
        }

        [Theory]
        [InlineData("after-rebuild-source-claim", false)]
        [InlineData("after-rebuild-source-claim", true)]
        [InlineData("before-recovery-marker", false)]
        [InlineData("before-recovery-marker", true)]
        [InlineData("after-source-backup", false)]
        [InlineData("after-source-backup", true)]
        [InlineData("after-temp-install", false)]
        [InlineData("after-temp-install", true)]
        [InlineData("after-rebuild-data-close", false)]
        [InlineData("after-rebuild-data-close", true)]
        public async Task Another_process_cannot_enter_until_replacement_finishes(string phase, bool encrypted)
        {
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            using (var owner = new MvccProcess("rebuild-ownership", file.Filename, password, phase))
            {
                await owner.Expect("ready");
                var alias = Path.Combine(Path.GetDirectoryName(file.Filename), ".", Path.GetFileName(file.Filename));
                await MvccProcess.Run("rebuild-probe", alias, password,
                    phase == "after-source-backup" || phase == "after-rebuild-data-close" ? "admission" : "physical");
                owner.Send("continue");
                await owner.Expect("done");
                await owner.Finish();
            }
            await MvccProcess.Run("rebuild-write", file.Filename, password);
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
            db.GetCollection("rows").FindById(4)["value"].AsString.Should().Be("contender");
        }

        [Theory]
        [InlineData("after-rebuild-source-claim", "direct", false)]
        [InlineData("after-rebuild-source-claim", "shared", true)]
        [InlineData("after-rebuild-source-claim", "coordinated", false)]
        [InlineData("after-temp-install", "direct", true)]
        [InlineData("after-temp-install", "coordinated", true)]
        [InlineData("after-rebuild-data-close", "shared", false)]
        [InlineData("after-rebuild-data-close", "coordinated", true)]
        public async Task Another_process_in_every_mode_waits_for_the_owner_and_its_write_survives(string phase, string mode, bool encrypted)
        {
            // The owner runs the recovery rebuild service, so no engine stays open after
            // it releases admission and the waiting process can open for writing.
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            using (var owner = new MvccProcess("rebuild-service-ownership", file.Filename, password, phase))
            {
                await owner.Expect("ready");
                using var opener = new MvccProcess("rebuild-waiting-write", file.Filename, password, mode);
                await opener.Expect("admitting");
                var opened = opener.ReadLine(TimeSpan.FromSeconds(20));
                // A third process is refused admission deterministically meanwhile.
                await MvccProcess.Run("rebuild-probe", file.Filename, password, "admission");
                opened.IsCompleted.Should().BeFalse("the " + mode + " opener cannot pass admission while the owner holds it");
                owner.Send("continue");
                await owner.Expect("done");
                await owner.Finish();
                (await opened).Should().Be("opened");
                await opener.Finish();
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetSuffixFile(logName, "-backup", false)).Should().Equal(log);
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
            using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password });
            db.GetCollection("rows").FindById(4)["value"].AsString.Should().Be("contender");
        }

        [Theory]
        [InlineData("after-rebuild-source-claim", false)]
        [InlineData("after-rebuild-source-claim", true)]
        [InlineData("before-recovery-marker", false)]
        [InlineData("before-recovery-marker", true)]
        public async Task Repeated_process_death_releases_claims_without_changing_original_pair(string phase, bool encrypted)
        {
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            for (var retry = 0; retry < 2; retry++)
            {
                using var owner = new MvccProcess("rebuild-ownership", file.Filename, password, phase);
                await owner.Expect("ready");
                await owner.Kill();
                File.ReadAllBytes(file.Filename).Should().Equal(data);
                File.ReadAllBytes(logName).Should().Equal(log);
                File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();
            }
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Password = password })) db.Rebuild();
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Abandoned_claim_does_not_bypass_the_durable_installation_marker(bool encrypted)
        {
            var password = encrypted ? "password" : null;
            using var file = RebuildOwnership_Tests.Seed(password);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            using (var owner = new MvccProcess("rebuild-ownership", file.Filename, password, "after-temp-install"))
            {
                await owner.Expect("ready");
                await owner.Kill();
            }
            for (var retry = 0; retry < 2; retry++)
            {
                Action open = () => new LiteDatabase(new ConnectionString
                {
                    Filename = file.Filename, Password = password, AutoRebuild = true
                }).Dispose();
                open.Should().Throw<LiteException>().Where(e => e.ErrorCode == LiteException.REBUILD_INCOMPLETE);
            }
            File.ReadAllBytes(FileHelper.GetSuffixFile(file.Filename, "-backup", false)).Should().Equal(data);
            File.ReadAllBytes(FileHelper.GetSuffixFile(logName, "-backup", false)).Should().Equal(log);
            File.Exists(logName).Should().BeFalse();
            // The child stopped after publishing a fully checkpointed candidate.
            // Only this known state permits the existing manual marker-removal protocol.
            File.Delete(RebuildRecovery.GetMarkerFilename(file.Filename));
            RebuildOwnership_Tests.VerifyAndWrite(file.Filename, password);
        }

        [Fact]
        public async Task Disabled_Unix_file_locks_fail_before_rebuilding()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = RebuildOwnership_Tests.Seed(null);
            var data = File.ReadAllBytes(file.Filename);
            var logName = FileHelper.GetLogFile(file.Filename);
            var log = File.ReadAllBytes(logName);
            using var child = new MvccProcess("rebuild-no-locks", file.Filename, null, disableFileLocking: true);
            await child.Expect("done");
            await child.Finish();
            File.ReadAllBytes(file.Filename).Should().Equal(data);
            File.ReadAllBytes(logName).Should().Equal(log);
            File.Exists(RebuildRecovery.GetMarkerFilename(file.Filename)).Should().BeFalse();
        }
    }
}
#endif
