#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmissionAliases_Tests
    {
        [Fact]
        public async Task Canonical_path_aliases_cannot_bypass_a_direct_owner()
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            var alias = Path.Combine(Path.GetDirectoryName(file), ".", Path.GetFileName(file));
            using (var db = new LiteDatabase(file))
            {
                await MvccProcess.Run("native-rejected", alias, null, "direct");
                await MvccProcess.Run("native-rejected", alias, null, "shared");
            }
            NativeAdmission_Tests.Verify(file);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Symlink_aliases_bind_storage_and_admission_to_the_target(bool directoryLink)
        {
            // Windows junctions/symlinks require runner privileges; lexical aliases
            // and hard links are exercised on Windows without that prerequisite.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            var link = file.Filename + "-alias";
            string alias;
            if (directoryLink)
            {
                Directory.CreateSymbolicLink(link, Path.GetDirectoryName(file));
                alias = Path.Combine(link, Path.GetFileName(file));
            }
            else
            {
                File.CreateSymbolicLink(link, file);
                alias = link;
            }
            try
            {
                using (var db = new LiteDatabase(file))
                    await MvccProcess.Run("native-rejected", alias, null, "shared");
                using (var shared = new LiteDatabase(new ConnectionString { Filename = alias, Connection = ConnectionType.Shared }))
                {
                    shared.GetCollection("rows").Count().Should().Be(1);
                    await MvccProcess.Run("native-open", file, null, "shared");
                    await MvccProcess.Run("native-rejected", file, null, "direct");
                    shared.Rebuild();
                    await MvccProcess.Run("native-rejected", alias, null, "direct");
                }
                NativeAdmission_Tests.Verify(file);
                if (!directoryLink) File.Exists(FileHelper.GetLogFile(alias)).Should().BeFalse();
            }
            finally
            {
                if (directoryLink) Directory.Delete(link);
                else File.Delete(link);
            }
        }

        [Fact]
        public void Hard_links_are_rejected_before_a_second_WAL_can_be_selected()
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            var alias = file.Filename + "-hardlink";
            var original = File.ReadAllBytes(file);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) CreateHardLinkW(alias, file, IntPtr.Zero).Should().BeTrue();
            else link(file, alias).Should().Be(0);
            try
            {
                foreach (var path in new[] { file.Filename, alias })
                {
                    Action open = () => { using var db = new LiteDatabase(path); };
                    open.Should().Throw<DatabaseAdmissionException>().WithMessage("*Hard-linked*");
                    File.Exists(FileHelper.GetLogFile(path)).Should().BeFalse();
                }
                File.ReadAllBytes(file).Should().Equal(original);
            }
            finally { File.Delete(alias); }
            NativeAdmission_Tests.Verify(file);
        }

        [Fact]
        public void Normalizing_an_old_file_alias_never_discards_its_WAL_or_recovery_marker()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var alias = new TempFile();
            using var target = new TempFile();
            using (var db = new LiteDatabase(alias))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            }
            var log = FileHelper.GetLogFile(alias);
            var committed = File.ReadAllBytes(log);
            File.Move(alias, target);
            File.CreateSymbolicLink(alias, target);
            var marker = RebuildRecovery.GetMarkerFilename(alias);
            try
            {
                Action open = () => { using var db = new LiteDatabase(alias); };
                open.Should().Throw<DatabaseAdmissionException>().WithMessage("*separate WAL*");
                File.ReadAllBytes(log).Should().Equal(committed);
                File.WriteAllText(marker, "incomplete old alias installation");
                open.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.REBUILD_INCOMPLETE);
                File.Delete(marker);
                File.Copy(log, FileHelper.GetLogFile(target));
                File.Delete(log);
                using var recovered = new LiteDatabase(alias);
                recovered.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
            }
            finally { File.Delete(marker); File.Delete(log); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_only_file_and_directory_need_no_writable_admission_storage(bool shared)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || Environment.UserName == "root") return;
            var directory = Path.Combine(Path.GetTempPath(), "litedb-native-permissions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "data.db");
            try
            {
                NativeAdmission_Tests.Seed(filename);
                var data = File.ReadAllBytes(filename);
                File.SetUnixFileMode(filename, UnixFileMode.UserRead);
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                using (var db = new LiteDatabase(new ConnectionString { Filename = filename, ReadOnly = true,
                    Connection = shared ? ConnectionType.Shared : ConnectionType.Direct }))
                    db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                File.ReadAllBytes(filename).Should().Equal(data);
                Directory.GetFiles(directory).Should().Equal(filename);
            }
            finally
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void A_dangling_symlink_cannot_create_a_target_guarded_by_a_rebuild_marker()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var alias = new TempFile();
            using var target = new TempFile();
            File.CreateSymbolicLink(alias, target);
            var marker = RebuildRecovery.GetMarkerFilename(target);
            File.WriteAllText(marker, "incomplete target installation");
            try
            {
                Action open = () => { using var db = new LiteDatabase(alias); };
                open.Should().Throw<IOException>().WithMessage("*unresolved symlink*");
                File.Exists(target).Should().BeFalse();
                File.Exists(FileHelper.GetLogFile(alias)).Should().BeFalse();
                File.ReadAllText(marker).Should().Be("incomplete target installation");
            }
            finally { File.Delete(marker); }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int link(string target, string alias);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string alias, string target, IntPtr security);
    }
}
#endif
