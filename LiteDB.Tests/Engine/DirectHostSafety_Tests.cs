using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection("DirectProcessHost")]
    public class DirectHostSafety_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-direct-safety-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectHostSafety_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void Failed_first_open_releases_native_admission_for_correct_password()
        {
            using (var db = new LiteDatabase(new ConnectionString { Filename = Filename, Password = "secret" }))
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            Action bad = () => { using var db = new LiteDatabase(Filename); };
            bad.Should().Throw<LiteException>();
            using var good = new LiteDatabase(new ConnectionString { Filename = Filename, Password = "secret" });
            Assert.NotNull(good.GetCollection("docs").FindById(1));
        }

        [Fact]
        public void Fatal_storage_failure_stops_all_facades_and_refuses_attach_until_last_release()
        {
            using (var seed = new LiteDatabase(Filename)) seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            using (var first = new LiteEngine(Filename))
            using (var second = new LiteEngine(Filename))
            {
                first.SimulateDiskWriteFail = page => throw new IOException("injected write failure");
                Action write = () => first.Insert("docs", new[] { new BsonDocument { ["_id"] = 2 } }, BsonAutoId.Int32);
                write.Should().Throw<IOException>();
                Action peer = () => second.Insert("docs", new[] { new BsonDocument { ["_id"] = 3 } }, BsonAutoId.Int32);
                peer.Should().Throw<IOException>();
                Action attach = () => { using var third = new LiteEngine(Filename); };
                attach.Should().Throw<IOException>();
            }
            using var cold = new LiteDatabase(Filename);
            Assert.NotNull(cold.GetCollection("docs").FindById(1));
            Assert.Null(cold.GetCollection("docs").FindById(2));
            Assert.Null(cold.GetCollection("docs").FindById(3));
        }

        [Fact]
        public async Task Independent_threads_commit_and_rollback_without_cross_facade_visibility()
        {
            using var first = new LiteDatabase(Filename);
            using var second = new LiteDatabase(Filename);
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 0 });
            using var begun = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var owner = Task.Run(() =>
            {
                first.BeginTrans();
                first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                begun.Set();
                if (!finished.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                first.Rollback().Should().BeTrue();
            });
            try
            {
                begun.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                Assert.Null(second.GetCollection("docs").FindById(1));
                second.GetCollection("other").Insert(new BsonDocument { ["_id"] = 2 });
            }
            finally { finished.Set(); }
            await owner;
            Assert.NotNull(first.GetCollection("other").FindById(2));
            Assert.Null(first.GetCollection("docs").FindById(1));
        }

        [Fact]
        public void Disposing_a_peer_does_not_rollback_the_surviving_facades_transaction()
        {
            using (var first = new LiteDatabase(Filename))
            using (var second = new LiteDatabase(Filename))
            {
                second.BeginTrans();
                second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                first.Dispose();
                second.Commit().Should().BeTrue();
            }
            using var cold = new LiteDatabase(Filename);
            Assert.NotNull(cold.GetCollection("docs").FindById(1));
        }

        [Fact]
        public void Reentrant_open_from_final_checkpoint_cannot_create_a_second_storage_core()
        {
            var observed = 0;
            var settings = new EngineSettings { Filename = Filename };
            settings.CheckpointStage = stage =>
            {
                if (Interlocked.Exchange(ref observed, 1) != 0) return;
                Action reenter = () => { using var other = new LiteEngine(Filename); };
                reenter.Should().Throw<InvalidOperationException>().WithMessage("*closing*");
            };
            using (var engine = new LiteEngine(settings))
                engine.Insert("docs", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            observed.Should().Be(1);
            using var cold = new LiteDatabase(Filename);
            Assert.NotNull(cold.GetCollection("docs").FindById(1));
        }

        [Fact]
        public void Unsupported_native_volume_fails_before_storage_initialization()
        {
            DatabaseFileIdentity.UnsupportedVolume = file => file == DatabaseFileIdentity.CanonicalPath(Filename);
            try
            {
                Action open = () => { using var db = new LiteDatabase(Filename); };
                open.Should().Throw<IOException>().WithMessage("*qualified local filesystem*");
                new FileInfo(Filename).Length.Should().Be(0);
            }
            finally { DatabaseFileIdentity.UnsupportedVolume = null; }
            using var good = new LiteDatabase(Filename);
            good.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
        }

#if !NETFRAMEWORK
        [Fact]
        public void Hard_links_are_refused_without_rewriting_data_or_creating_alias_wal()
        {
            using (var seed = new LiteDatabase(Filename)) seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var alias = Path.Combine(_directory, "hardlink.db");
            var windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            (windows ? CreateHardLink(alias, Filename, IntPtr.Zero) : Link(Filename, alias) == 0).Should().BeTrue();
            var before = File.ReadAllBytes(Filename);
            Action open = () => { using var db = new LiteDatabase(alias); };
            open.Should().Throw<IOException>().WithMessage("*Hard-linked*");
            File.ReadAllBytes(Filename).Should().Equal(before);
            File.Exists(FileHelper.GetLogFile(alias)).Should().BeFalse();
            File.Delete(alias);
            using var restored = new LiteDatabase(Filename);
            Assert.NotNull(restored.GetCollection("docs").FindById(1));
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int Link(string original, string alias);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string alias, string original, IntPtr reserved);

        [Fact]
        public void Symlink_aliases_share_canonical_storage_and_separate_alias_wal_is_rejected()
        {
            using var first = new LiteDatabase(Filename);
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var alias = Path.Combine(_directory, "alias.db");
            File.CreateSymbolicLink(alias, Filename);
            using (var second = new LiteDatabase(alias)) Assert.NotNull(second.GetCollection("docs").FindById(1));
            File.WriteAllBytes(FileHelper.GetLogFile(alias), new byte[] { 1, 2, 3 });
            Action open = () => { using var rejected = new LiteDatabase(alias); };
            open.Should().Throw<IOException>().WithMessage("*separate WAL*");
            Assert.NotNull(first.GetCollection("docs").FindById(1));
        }
#endif
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
