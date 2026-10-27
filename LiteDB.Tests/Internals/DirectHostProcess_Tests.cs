#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class DirectHostProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-direct-process-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectHostProcess_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task Last_facade_release_controls_cross_process_admission_and_unrelated_handle_close_does_not_unlock()
        {
            var first = new LiteDatabase(Filename);
            var second = new LiteDatabase(Filename);
            try
            {
                first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                // Classic POSIX process locks would be lost by this close.
                using (var unrelated = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    unrelated.Length.Should().BeGreaterThan(0);
                var files = Directory.GetFiles(_directory);
                var data = File.ReadAllBytes(Filename);
                await MvccProcess.Run("direct-host-direct-refused", Filename, null);
                await MvccProcess.Run("direct-host-shared-refused", Filename, null);
                File.ReadAllBytes(Filename).Should().Equal(data);
                Directory.GetFiles(_directory).Should().BeEquivalentTo(files);
                first.Dispose();
                await MvccProcess.Run("direct-host-direct-refused", Filename, null);
            }
            finally { first.Dispose(); second.Dispose(); }
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }

        [Fact]
        public async Task Shared_reader_keeps_native_exclusion_after_its_facade_disposes()
        {
            using (var seed = new LiteDatabase(Filename))
                seed.GetCollection("docs").Insert(System.Linq.Enumerable.Range(0, 256).Select(id =>
                    new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 2048) }));
            using var shared = new SharedEngine(new LiteDB.Engine.EngineSettings { Filename = Filename });
            using (var reader = shared.Query("docs", new Query()))
            {
                reader.Read().Should().BeTrue();
                shared.Dispose();
                await MvccProcess.Run("direct-host-direct-refused", Filename, null);
                var count = 1;
                while (reader.Read()) count++;
                count.Should().Be(256);
            }
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }

        [Fact]
        public async Task Abandoned_facade_does_not_strand_a_live_peers_reference_count()
        {
            var abandoned = Abandon(Filename);
            using (var peer = new LiteDatabase(Filename))
            {
                for (var pass = 0; pass < 3; pass++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                abandoned.IsAlive.Should().BeFalse();
                Assert.NotNull(peer.GetCollection("docs").FindById(1));
            }
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(string filename)
        {
            var db = new LiteDatabase(filename);
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            return new WeakReference(db);
        }

        [Fact]
        public async Task Rebuild_transfers_exclusion_to_the_replacement_inode()
        {
            using (var owner = new LiteDatabase(Filename))
            {
                owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                owner.Rebuild();
                await MvccProcess.Run("direct-host-direct-refused", Filename, null);
                await MvccProcess.Run("direct-host-shared-refused", Filename, null);
                using var attached = new LiteDatabase(Filename);
                Assert.NotNull(attached.GetCollection("docs").FindById(1));
            }
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData(null, true)]
        [InlineData("secret", false)]
        [InlineData("secret", true)]
        public async Task Process_kill_releases_native_lock_and_recovers_only_acknowledged_data(string password, bool disableFileLocking)
        {
            using (var owner = new MvccProcess("direct-host-hold", Filename, password, disableFileLocking: disableFileLocking))
            {
                await owner.Expect("ready");
                await MvccProcess.Run("direct-host-direct-refused", Filename, password);
                await MvccProcess.Run("direct-host-shared-refused", Filename, password);
                await owner.Kill();
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var recovered = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password });
                recovered.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("acknowledged");
                Assert.Null(recovered.GetCollection("docs").FindById(2));
                recovered.GetCollection("docs").FindOne(Query.EQ("value", "acknowledged"))["_id"].AsInt32.Should().Be(1);
            }
            await MvccProcess.Run("direct-host-direct-open", Filename, password);
        }

        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
#endif
