#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class DirectHostAdmission_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-admission-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectHostAdmission_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Direct_durable_open_writers_checkpoint_close_and_reopen_finish_in_a_bounded_child(string password)
        {
            // Run on Windows too. A stuck native call or worker is killed by
            // MvccProcess.Dispose instead of leaving the test host hung.
            using var child = new MvccProcess("direct-host-durable-liveness", Filename, password);
            foreach (var writers in new[] { 1, 4, 8 })
                foreach (var stage in new[] { "opened", "indexed", "committed", "checkpointed", "closed", "reopened" })
                {
                    try { await child.Expect(stage + "-" + writers); }
                    catch (TimeoutException error)
                    {
                        throw new TimeoutException("Direct child stalled before " + stage + " with " + writers + " writers.", error);
                    }
                }
            await child.Expect("done");
            await child.Finish();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Native_admission_coexists_with_managed_streams_without_losing_exclusion(bool shared)
        {
            var settings = new EngineSettings { Filename = Filename };
            using (var admission = NativeAdmission.Open(settings, shared))
            {
                var expected = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
                using (var writer = new FileStream(Filename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    writer.Write(expected, 0, expected.Length);
                    writer.Flush();
                    TempFile.ReadAllBytesShared(Filename).Should().Equal(expected);
                }
                using var probe = new DatabaseFileLock(settings.Filename, false, false);
                probe.Conflicts(DatabaseFileLock.Admission).Should().BeTrue("closing unrelated streams must not release admission");
                Action conflicting = () => probe.Lock(DatabaseFileLock.Admission, exclusive: true);
                conflicting.Should().Throw<IOException>();
                admission.Dispose();
                probe.Conflicts(DatabaseFileLock.Admission).Should().BeFalse();
                probe.Lock(DatabaseFileLock.Admission, exclusive: true);
                TempFile.ReadAllBytesShared(Filename).Should().Equal(expected);
            }
        }

        [Theory]
        [InlineData(ConnectionType.Direct, null)]
        [InlineData(ConnectionType.Direct, "secret")]
        [InlineData(ConnectionType.Shared, null)]
        [InlineData(ConnectionType.Shared, "secret")]
        public void New_database_open_write_rollback_and_read_only_reopen_preserve_payloads_and_indexes(ConnectionType connection, string password)
        {
            var settings = new ConnectionString { Filename = Filename, Password = password, Connection = connection };
            using (var first = new LiteDatabase(settings))
            using (var second = new LiteDatabase(settings))
            {
                first.CheckpointSize = 0;
                first.GetCollection("docs").EnsureIndex("value");
                first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed", ["payload"] = new string('a', 9000) });
                second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "unrelated" });
                first.BeginTrans().Should().BeTrue();
                first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3, ["value"] = "aborted" });
                first.Rollback().Should().BeTrue();
            }
            var before = File.ReadAllBytes(Filename);
            settings.ReadOnly = true;
            using (var first = new LiteDatabase(settings))
            using (var second = new LiteDatabase(settings))
            {
                var committed = first.GetCollection("docs").FindOne(Query.EQ("value", "committed"));
                committed["_id"].AsInt32.Should().Be(1);
                committed["payload"].AsString.Should().Be(new string('a', 9000));
                second.GetCollection("docs").FindById(2)["value"].AsString.Should().Be("unrelated");
                Assert.Null(second.GetCollection("docs").FindById(3));
                first.GetCollection("docs").Count().Should().Be(2);
            }
            File.ReadAllBytes(Filename).Should().Equal(before);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Shared_processes_can_attach_while_native_admission_still_excludes_Direct(string password)
        {
            using (var child = new MvccProcess("direct-host-shared-admission-hold", Filename, password))
            {
                await child.Expect("ready");
                using (var peer = new LiteDatabase(new ConnectionString
                    { Filename = Filename, Password = password, Connection = ConnectionType.Shared }))
                {
                    peer.GetCollection("docs").FindOne(Query.EQ("value", "child"))["_id"].AsInt32.Should().Be(1);
                    peer.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "peer" });
                    await MvccProcess.Run("direct-host-direct-refused", Filename, password);
                }
                child.Send("continue");
                await child.Expect("done");
                await child.Finish();
            }
            using var reopened = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password });
            reopened.GetCollection("docs").Count().Should().Be(2);
            reopened.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("child");
            reopened.GetCollection("docs").FindOne(Query.EQ("value", "peer"))["_id"].AsInt32.Should().Be(2);
        }

        [Fact]
        public void Unix_lock_file_is_persistent_never_truncated_and_refuses_symlinks()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            File.WriteAllBytes(Filename, new byte[] { 1, 2, 3 });
            var identity = "test-identity";
            var path = DatabaseLockFile.Filename(Filename, identity);
            string firstIdentity;
            using (var first = DatabaseLockFile.Open(Filename, identity, false)) firstIdentity = DatabaseFileIdentity.Read(first);
            File.Exists(path).Should().BeTrue();
            File.WriteAllBytes(path, new byte[] { 4, 5, 6 });
            using (var second = DatabaseLockFile.Open(Filename, identity, true))
                DatabaseFileIdentity.Read(second).Should().Be(firstIdentity);
            File.ReadAllBytes(path).Should().Equal(new byte[] { 4, 5, 6 });
            File.Delete(path); // No participant remains; replace it with an unsafe alias.
            File.CreateSymbolicLink(path, Filename);
            Action open = () => { using var refused = DatabaseLockFile.Open(Filename, identity, false); };
            open.Should().Throw<IOException>();
            File.ReadAllBytes(Filename).Should().Equal(new byte[] { 1, 2, 3 });
            File.Delete(path);
            MkFifo(path, 0x180).Should().Be(0); // 0600; O_NONBLOCK must prevent a read-only open hanging.
            Action openPipe = () => { using var refused = DatabaseLockFile.Open(Filename, identity, true); };
            openPipe.Should().Throw<IOException>().WithMessage("*regular file*");
            var lockDirectory = Path.GetDirectoryName(path);
            Directory.Delete(lockDirectory, true); // No descriptor remains.
            Directory.CreateSymbolicLink(lockDirectory, _directory);
            Action openDirectoryAlias = () => { using var refused = DatabaseLockFile.Open(Filename, identity, false); };
            openDirectoryAlias.Should().Throw<IOException>().WithMessage("*real directory*");
            File.Exists(Path.Combine(_directory, identity + ".lock")).Should().BeFalse();
        }

        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
        private static extern int MkFifo(string path, int mode);

        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
#endif
