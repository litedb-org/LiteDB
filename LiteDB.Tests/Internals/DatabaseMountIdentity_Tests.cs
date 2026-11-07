using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection("DirectProcessHost")]
    public class DatabaseMountIdentity_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Mount_identity_is_rechecked_and_file_bind_mounts_are_refused_without_mutation(bool newFile)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            using var file = new TempFile();
            if (!newFile)
            {
                using var seed = new LiteDatabase(file.Filename);
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "keep" });
            }
            var before = File.Exists(file.Filename) ? File.ReadAllBytes(file.Filename) : null;
            var original = DatabaseUnixNative.Api.MountIdentity;
            var fileMount = false;
            var calls = 0;
            DatabaseUnixNative.Api.MountIdentity = (int directory, string path, int flags, uint mask, out DatabaseMountIdentity.Statx data) =>
            {
                calls++;
                data = new DatabaseMountIdentity.Statx { Mask = 0x1000, Mount = fileMount && path == file.Filename ? 9ul : 7ul };
                return 0;
            };
            try
            {
                DatabaseMountIdentity.IsFileMount(file.Filename).Should().BeFalse();
                fileMount = true;
                Action open = () => { using var db = new LiteDatabase(file.Filename); };
                open.Should().Throw<IOException>().WithMessage("*File-only bind mounts*");
                calls.Should().Be(4);
                if (before == null) File.Exists(file.Filename).Should().BeFalse();
                else File.ReadAllBytes(file.Filename).Should().Equal(before);
                fileMount = false;
                using var good = new LiteDatabase(file.Filename);
                if (newFile) good.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "keep" });
                good.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("keep");
            }
            finally { DatabaseUnixNative.Api.MountIdentity = original; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Old_libc_or_kernel_without_mount_ids_uses_existing_mountinfo_policy(bool missingEntryPoint)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            using var file = new TempFile();
            var original = DatabaseUnixNative.Api.MountIdentity;
            DatabaseUnixNative.Api.MountIdentity = (int directory, string path, int flags, uint mask, out DatabaseMountIdentity.Statx data) =>
            {
                if (missingEntryPoint) throw new EntryPointNotFoundException();
                data = default;
                return 0;
            };
            try
            {
                DatabaseMountIdentity.IsFileMount(file.Filename).Should().BeNull();
                using (var db = new LiteDatabase(file.Filename))
                    db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
                using var reopened = new LiteDatabase(file.Filename);
                Assert.NotNull(reopened.GetCollection("rows").FindById(1));
            }
            finally { DatabaseUnixNative.Api.MountIdentity = original; }
        }
#if !NETFRAMEWORK
        [Theory]
        [InlineData(38, true)]
        [InlineData(22, true)]
        [InlineData(95, true)]
        [InlineData(13, false)]
        [InlineData(5, false)]
        public void Unsupported_mount_id_calls_fall_back_but_io_errors_fail_closed(int error, bool fallback)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            using var file = new TempFile();
            var original = DatabaseUnixNative.Api.MountIdentity;
            DatabaseUnixNative.Api.MountIdentity = (int directory, string path, int flags, uint mask, out DatabaseMountIdentity.Statx data) =>
            {
                data = default;
                Marshal.SetLastPInvokeError(error);
                return -1;
            };
            try
            {
                if (fallback) DatabaseMountIdentity.IsFileMount(file.Filename).Should().BeNull();
                else
                {
                    Action open = () => { using var db = new LiteDatabase(file.Filename); };
                    open.Should().Throw<IOException>().WithMessage("*statx database mount*");
                    File.Exists(file.Filename).Should().BeFalse();
                }
            }
            finally { DatabaseUnixNative.Api.MountIdentity = original; }
        }
#endif
    }
}
