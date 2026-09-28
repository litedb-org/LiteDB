#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmissionWindowsPermissions_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Read_only_admission_works_with_file_and_directory_write_access_denied(bool shared)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            var directory = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "litedb-native-acl-" + Guid.NewGuid().ToString("N")));
            directory.Create();
            var file = new FileInfo(Path.Combine(directory.FullName, "data.db"));
            NativeAdmission_Tests.Seed(file.FullName);
            var original = File.ReadAllBytes(file.FullName);
            var directorySecurity = directory.GetAccessControl();
            var fileSecurity = file.GetAccessControl();
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var deniedDirectory = directory.GetAccessControl();
                deniedDirectory.AddAccessRule(new FileSystemAccessRule(identity.User,
                    FileSystemRights.Write, AccessControlType.Deny));
                directory.SetAccessControl(deniedDirectory);
                var deniedFile = file.GetAccessControl();
                deniedFile.AddAccessRule(new FileSystemAccessRule(identity.User,
                    FileSystemRights.Write, AccessControlType.Deny));
                file.SetAccessControl(deniedFile);
                using (var db = new LiteDatabase(new ConnectionString
                    { Filename = file.FullName, ReadOnly = true, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct }))
                    db.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                File.ReadAllBytes(file.FullName).Should().Equal(original);
                Directory.GetFiles(directory.FullName).Should().Equal(file.FullName);
            }
            finally
            {
                file.SetAccessControl(fileSecurity);
                directory.SetAccessControl(directorySecurity);
                directory.Delete(true);
            }
        }
    }
}
#endif
