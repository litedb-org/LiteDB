#if DEBUG || TESTING
using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    /// <summary>
    /// The directory sync binds the C library through <see cref="NativeLibc"/>, with the same
    /// soname fallback as file syncs, and classifies errno like them. Windows needs nothing.
    /// </summary>
    [Collection(NativeFileSyncCollection.Name)]
    public class DirectorySync_Tests
    {
        [Fact]
        public void Directory_sync_uses_the_bound_c_library_and_classifies_errno()
        {
            var directory = Path.GetTempPath();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                NativeFileSync.SyncDirectory(directory);
                return;
            }

            NativeLibc.LibraryName.Should().NotBeNull();
            NativeFileSync.SyncDirectory(directory);

            Action missing = () => NativeFileSync.SyncDirectory(Path.Combine(directory, "litedb-missing-" + Guid.NewGuid().ToString("N")));
            missing.Should().Throw<FileSyncException>().Which.IsUnsupported.Should().BeFalse("ENOENT is a failure, not storage that cannot sync");
        }
    }
}
#endif
