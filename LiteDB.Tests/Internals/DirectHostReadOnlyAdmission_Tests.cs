#if !NETFRAMEWORK
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class DirectHostReadOnlyAdmission_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-read-admission-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectHostReadOnlyAdmission_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public void Read_only_file_and_directory_preserve_payloads_indexes_and_bytes(string password, bool readOnlyDirectory)
        {
            Seed(password);
            var before = File.ReadAllBytes(Filename);
            var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            // Remove Darwin's previously seeded lock directory while no participant
            // remains, so the unwritable-directory case exercises first admission.
            var locks = Path.Combine(_directory, ".litedb-admission");
            if (readOnlyDirectory && Directory.Exists(locks)) Directory.Delete(locks, true);
            if (windows) File.SetAttributes(Filename, FileAttributes.ReadOnly);
            else
            {
                File.SetUnixFileMode(Filename, (UnixFileMode)0x124); // 0444
                if (readOnlyDirectory) File.SetUnixFileMode(_directory, (UnixFileMode)0x16d); // 0555
                Action writable = () => { using var denied = new FileStream(Filename, FileMode.Open, FileAccess.ReadWrite); };
                writable.Should().Throw<UnauthorizedAccessException>("the fixture must actually deny write access");
            }
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var first = OpenReadOnly(password);
                    using var peer = OpenReadOnly(password);
                    Verify(first);
                    Verify(peer);
                }
                File.ReadAllBytes(Filename).Should().Equal(before);
                File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();
                if (readOnlyDirectory) Directory.Exists(locks).Should().BeFalse();
            }
            finally { RestorePermissions(); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Read_only_Direct_excludes_writable_and_read_only_process_modes_until_last_release(string password)
        {
            Seed(password);
            var before = File.ReadAllBytes(Filename);
            var first = OpenReadOnly(password);
            var peer = OpenReadOnly(password);
            try
            {
                using (var unrelated = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    unrelated.Length.Should().BeGreaterThan(0);
                foreach (var mode in new[] { "direct", "shared", "readonly-direct", "readonly-shared" })
                    await MvccProcess.Run("direct-host-" + mode + "-refused", Filename, password);
                first.Dispose();
                await MvccProcess.Run("direct-host-shared-refused", Filename, password);
                Verify(peer);
            }
            finally { first.Dispose(); peer.Dispose(); }
            File.ReadAllBytes(Filename).Should().Equal(before);
            await MvccProcess.Run("direct-host-direct-open", Filename, password);
            using var reopened = OpenReadOnly(password);
            Verify(reopened);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public async Task Writable_process_modes_exclude_read_only_Direct(ConnectionType connection)
        {
            Seed(null);
            using (var writer = new LiteDatabase(new ConnectionString { Filename = Filename, Connection = connection }))
            {
                await MvccProcess.Run("direct-host-readonly-direct-refused", Filename, null);
                Verify(writer);
            }
            using var reopened = OpenReadOnly(null);
            Verify(reopened);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Darwin_unavailable_sibling_requires_a_non_writable_data_file(bool immutable)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
            Seed(null);
            var identity = DatabaseFileIdentity.CanonicalPath(Filename);
            string lockFile;
            using (var file = new DatabaseFileLock(Filename, true, false))
                lockFile = DatabaseLockFile.Filename(identity, file.Identity);
            var before = File.ReadAllBytes(Filename);
            File.SetUnixFileMode(lockFile, (UnixFileMode)0); // Existing sibling cannot even be read.
            if (immutable) File.SetUnixFileMode(Filename, (UnixFileMode)0x124);
            try
            {
                if (immutable)
                {
                    using var db = OpenReadOnly(null);
                    Verify(db);
                }
                else
                {
                    Action open = () => { using var db = OpenReadOnly(null); };
                    open.Should().Throw<IOException>();
                }
                File.ReadAllBytes(Filename).Should().Equal(before);
            }
            finally
            {
                File.SetUnixFileMode(lockFile, (UnixFileMode)0x180);
                RestorePermissions();
            }
            using var fresh = OpenReadOnly(null);
            Verify(fresh);
        }

        private LiteDatabase OpenReadOnly(string password) => new LiteDatabase(new ConnectionString
            { Filename = Filename, Password = password, ReadOnly = true });

        private void Seed(string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password });
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 17, ["value"] = "committed", ["payload"] = new string('q', 12366) });
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 99, ["value"] = "unrelated" });
            db.BeginTrans();
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 18, ["value"] = "aborted" });
            db.Rollback();
        }

        private static void Verify(LiteDatabase db)
        {
            db.GetCollection("docs").Count().Should().Be(1);
            var doc = db.GetCollection("docs").FindOne(Query.EQ("value", "committed"));
            doc["_id"].AsInt32.Should().Be(17);
            doc["payload"].AsString.Should().Be(new string('q', 12366));
            Assert.Null(db.GetCollection("docs").FindById(18));
            db.GetCollection("sentinel").FindById(99)["value"].AsString.Should().Be("unrelated");
        }

        private void RestorePermissions()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) File.SetAttributes(Filename, FileAttributes.Normal);
            else
            {
                File.SetUnixFileMode(_directory, (UnixFileMode)0x1c0); // 0700
                File.SetUnixFileMode(Filename, (UnixFileMode)0x180); // 0600
            }
        }

        public void Dispose()
        {
            if (File.Exists(Filename)) RestorePermissions();
            WindowsFileRelease.DeleteDirectory(_directory);
        }
    }
}
#endif
