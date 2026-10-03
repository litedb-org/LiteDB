#if !NETFRAMEWORK
using System;
using System.Linq;
using System.Threading;
using LiteDB.Tests.Issues;
using System.Threading.Tasks;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class TransactionHandleProcess_Tests
    {
        [Fact]
        public async Task First_shared_handle_preserves_caller_culture_and_existing_persisted_collation()
        {
            using var file = new TempFile();
            await MvccProcess.Run("handle-first-culture", file, null, "shared");
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
            using var tx = db.BeginTransaction();
            Assert.Equal(1, tx.GetCollection("rows").Find(Query.EQ("value", "ı")).Count());
            Assert.Empty(tx.GetCollection("rows").Find(Query.EQ("value", "i")));
            tx.Commit();
            Assert.Equal("tr-TR", db.Collation.Culture.Name);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Session_close_progresses_while_callers_exhaust_the_thread_pool(bool shared, bool active)
        {
            using var file = new TempFile();
            Seed(file, null);
            await MvccProcess.Run(active ? "handle-close-active" : "handle-close-idle", file, null,
                shared ? "shared" : "direct");
            Verify(file, null, new[] { 1 });
        }

        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "secret")]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public async Task Process_death_releases_handle_ownership_without_committing_partial_wal(bool shared, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var holder = new MvccProcess("handle-hold", file, password, shared ? "shared" : "direct"))
            {
                await holder.Expect("ready");
                await holder.Kill();
            }
            Verify(file, password, new[] { 1 });
            Verify(file, password, new[] { 1 });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Process_death_during_sequential_shared_handle_preserves_committed_model(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var holder = new MvccProcess("handle-hold-sequential", file, password, "shared"))
            {
                await holder.Expect("ready");
                await holder.Kill();
            }
            Verify(file, password, new[] { 1 });
            Verify(file, password, new[] { 1 });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Acknowledged_shared_commit_survives_death_before_session_checkpoint(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var holder = new MvccProcess("handle-hold", file, password, "shared"))
            {
                await holder.Expect("ready");
                holder.Send("commit");
                await holder.Expect("done");
                Assert.True(new System.IO.FileInfo(FileHelper.GetLogFile(file)).Length > 0,
                    "the acknowledged transaction must still require WAL recovery before session checkpoint");
                await holder.Kill();
            }
            Verify(file, password, new[] { 1, 2 });
            Verify(file, password, new[] { 1, 2 });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Independent_process_waits_for_shared_handle_then_acquires_before_handle_disposal(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using var holder = new MvccProcess("handle-hold", file, password, "shared");
            await holder.Expect("ready");
            using var writer = new MvccProcess("handle-writer", file, password, "shared");
            await writer.Expect("begin");
            var written = writer.Expect("done");
            // The child is about to wait for native admission; it must not complete meanwhile.
            await Task.Delay(1000);
            Assert.False(written.IsCompleted);
            holder.Send("commit");
            await holder.Expect("done");
            await written;
            await writer.Finish();
            await holder.Finish(release: true);
            Verify(file, password, new[] { 1, 2, 3 });
            Verify(file, password, new[] { 1, 2, 3 });
        }

        [Fact]
        public async Task Begin_waits_for_remote_owner_and_sees_its_commit()
        {
            using var file = new TempFile();
            Seed(file, null);
            using var holder = new MvccProcess("handle-hold", file, null, "shared");
            await holder.Expect("ready");
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
            var pending = Task.Run(() =>
            {
                using var tx = db.BeginTransaction();
                Assert.NotNull(tx.GetCollection("rows").FindById(2));
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = 126 });
                tx.Commit();
            });
            await Task.Delay(500);
            Assert.False(pending.IsCompleted);
            holder.Send("commit");
            await holder.Expect("done");
            await pending.WaitAsync(TimeSpan.FromSeconds(20));
            await holder.Finish(release: true);
            db.Dispose();
            Verify(file, null, new[] { 1, 2, 3 });
        }

        private static void Seed(string file, string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.GetCollection("rows").EnsureIndex("value", true);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
        }
        private static void Verify(string file, string password, int[] ids)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            Assert.Equal(ids, db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
            Assert.Equal(ids.Length, db.GetCollection("rows").Find(Query.GTE("value", 42)).Count());
            Assert.NotNull(db.GetCollection("sentinel").FindById(9));
        }
    }
}
#endif
