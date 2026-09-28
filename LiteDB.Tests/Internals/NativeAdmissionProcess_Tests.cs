#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using LiteDB.Tests.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class NativeAdmissionProcess_Tests
    {
        [Theory]
        [InlineData("direct", "direct")]
        [InlineData("direct", "shared")]
        [InlineData("shared", "direct")]
        [InlineData("direct", "direct-readonly")]
        [InlineData("direct-readonly", "direct")]
        [InlineData("shared-readonly", "direct")]
        [InlineData("direct-readonly", "shared")]
        public async Task Incompatible_processes_cannot_mutate_storage_and_owner_death_releases_admission(string owner, string contender)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            var data = File.ReadAllBytes(file);
            for (var iteration = 0; iteration < 2; iteration++)
            {
                using var holder = new MvccProcess("native-hold", file, null, owner);
                await holder.Expect("ready");
                await MvccProcess.Run("native-rejected", file, null, contender);
                TempFile.ReadAllBytesShared(file).Should().Equal(data);
                await holder.Kill();
                await MvccProcess.Run("native-open", file, null, contender);
            }
            NativeAdmission_Tests.Verify(file);
        }

        [Fact]
        public async Task Many_shared_processes_coexist_until_the_last_native_lease_ends()
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            var processes = Enumerable.Range(0, 5).Select(_ => new MvccProcess("native-hold", file, null, "shared")).ToArray();
            try
            {
                foreach (var process in processes) await process.Expect("ready");
                for (var i = 0; i < processes.Length; i++)
                {
                    await MvccProcess.Run("native-rejected", file, null, "direct");
                    await processes[i].Kill();
                }
                await MvccProcess.Run("native-open", file, null, "direct");
            }
            finally { foreach (var process in processes) process.Dispose(); }
            NativeAdmission_Tests.Verify(file);
        }

        [Fact]
        public async Task Remaining_local_reference_excludes_other_processes_after_engine_disposal()
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using var db = new LiteEngine(file);
            using var first = SharedModeGuard.Open(file, false, SharedMutexNameStrategy.Default);
            using var last = SharedModeGuard.Open(file, false, SharedMutexNameStrategy.Default);
            db.Dispose();
            await Task.Run(() => first.Dispose());
            await MvccProcess.Run("native-rejected", file, null, "direct");
            await Task.Run(() => last.Dispose());
            await MvccProcess.Run("native-open", file, null, "direct");
            NativeAdmission_Tests.Verify(file);
        }

        [Theory]
        [InlineData("before-log-backup")]
        [InlineData("after-source-backup")]
        [InlineData("after-temp-install")]
        public async Task Replacement_never_exposes_an_unlocked_live_file(string stage)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using var child = new MvccProcess("native-rebuild-hold", file, null, stage);
            await child.Expect("ready");
            await MvccProcess.Run("native-rejected", file, null, "shared");
            await MvccProcess.Run("native-rejected", file, null, "direct");
            child.Send("continue");
            await child.Expect("installed");
            await MvccProcess.Run("native-rejected", file, null, "direct");
            await MvccProcess.Run("native-rejected", file, null, "shared");
            await child.Finish(release: true);
            NativeAdmission_Tests.Verify(file);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Shared_replacement_requires_remote_idle_connections_to_close(string password)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file, password);
            using (var db = new LiteDatabase(new ConnectionString
                { Filename = file, Password = password, Connection = ConnectionType.Shared }))
            {
                db.GetCollection("rows").Count().Should().Be(1);
                using (var child = new MvccProcess("native-hold", file, password, "shared"))
                {
                    await child.Expect("ready");
                    Action rebuild = () => db.Rebuild();
                    rebuild.Should().Throw<IOException>().WithMessage("*Close other processes*");
                    File.Exists(FileHelper.GetSuffixFile(file, "-temp", false)).Should().BeFalse();
                    await child.Kill();
                }
            }
            using (var db = new LiteDatabase(new ConnectionString
                { Filename = file, Password = password, Connection = ConnectionType.Shared }))
            {
                db.Rebuild();
                await MvccProcess.Run("native-rejected", file, password, "direct");
                await MvccProcess.Run("native-open", file, password, "shared");
            }
            NativeAdmission_Tests.Verify(file, password);
        }
    }
}
#endif
