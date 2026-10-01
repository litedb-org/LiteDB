using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The <c>ILiteDatabase</c> extension refuses a database that is not a transaction-handle
    /// provider before touching it; a non-fatal handle statement failure leaves peer sessions
    /// working; a handle works on a Shared connection in coordinated read mode.
    /// </summary>
    public class TransactionHandleProviderAndPeer_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

        private static int[] Ids(LiteDatabase db) =>
            db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();

        [Fact]
        public void Extension_rejects_a_non_provider_database_before_side_effects()
        {
            var database = new NonProviderDatabase();
            Assert.False(database is ILiteTransactionProvider);
            var refusal = Assert.Throws<NotSupportedException>(() => database.BeginTransaction());
            Assert.Contains("does not support thread-independent transaction handles", refusal.Message);
            Assert.Empty(database.Calls);
            Assert.Throws<ArgumentNullException>(() => ((ILiteDatabase)null).BeginTransaction());
        }

        [Fact]
        public void Non_provider_stub_records_every_member()
        {
            // Positive control for the test above: any member access would have been recorded.
            ILiteDatabase database = new NonProviderDatabase();
            var calls = ((NonProviderDatabase)database).Calls;
            var methods = typeof(ILiteDatabase).GetMethods().Concat(typeof(IDisposable).GetMethods()).ToArray();
            foreach (var method in methods)
            {
                var target = method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(BsonDocument)) : method;
                var arguments = target.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
                var before = calls.Count;
                target.Invoke(database, arguments);
                Assert.True(calls.Count == before + 1, method.Name + " was not recorded.");
            }
            Assert.True(methods.Length >= 30, "ILiteDatabase members: " + methods.Length);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Peer_session_is_unaffected_by_a_handle_statement_failure(bool shared)
        {
            using var file = new TempFile();
            LiteEngine direct = null;
            LiteDatabase Open() => shared
                ? new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared })
                : new LiteDatabase(direct, disposeOnClose: false);
            if (!shared) direct = new LiteEngine(new EngineSettings { Filename = file });
            try
            {
                using (var db = Open())
                using (var peer = Open())
                {
                    db.GetCollection("rows").Insert(Row(1));
                    db.GetCollection("rows").EnsureIndex("value", true);
                    db.Timeout = peer.Timeout = TimeSpan.FromSeconds(5);
                    var tx = db.BeginTransaction();
                    tx.GetCollection("rows").Insert(Row(2));
                    // Duplicate key: a statement failure, not an engine failure.
                    var failure = Assert.Throws<LiteException>(() => tx.GetCollection("rows").Insert(Row(1)));
                    Assert.Equal(LiteException.INDEX_DUPLICATE_KEY, failure.ErrorCode);
                    Assert.Equal(LiteTransactionState.Failed, tx.State);

                    // The peer reads, writes and begins a new handle without waiting for the failed one.
                    var work = Task.Run(() =>
                    {
                        Assert.Equal(new[] { 1 }, Ids(peer));
                        peer.GetCollection("rows").Insert(Row(3));
                        using var next = peer.BeginTransaction();
                        Assert.Equal(new[] { 1, 3 }, next.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
                        next.GetCollection("rows").Insert(Row(4));
                        next.Commit();
                    });
                    Assert.True(work.Wait(TimeSpan.FromSeconds(20)), "The peer waited for the failed handle.");
                    work.GetAwaiter().GetResult();
                    Assert.Equal(0, db.TransactionHandles.ActiveCount);
                    // The failing connection itself keeps working too.
                    using (var again = db.BeginTransaction())
                    {
                        again.GetCollection("rows").Insert(Row(5));
                        again.Commit();
                    }
                    Assert.Equal(new[] { 1, 3, 4, 5 }, Ids(db));
                    Assert.Throws<InvalidOperationException>(() => tx.Commit());
                    tx.Dispose();
                }
            }
            finally { direct?.Dispose(); }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var cold = new LiteDatabase(file);
                Assert.Equal(new[] { 1, 3, 4, 5 }, Ids(cold));
                Assert.Equal(4, cold.GetCollection("rows").Count(Query.GTE("value", 0)));
            }
        }

#if NET8_0_OR_GREATER
        [Fact]
        public void Handle_works_on_a_coordinated_read_connection()
        {
            if (SharedMappedDirectory.SkipReason != null)
            {
                // Coordinated reads need a qualified volume; without one the connection never
                // enters coordinated mode. Positive control: the reason is reported.
                Assert.False(string.IsNullOrWhiteSpace(SharedMappedDirectory.SkipReason));
                return;
            }
            var directory = Path.Combine(SharedMappedDirectory.Root, "litedb-handle-coordinated-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var file = Path.Combine(directory, "test.db");
                using (var writer = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
                using (var engine = new SharedEngine(new EngineSettings { Filename = file }) { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
                using (var reader = new LiteDatabase(engine, disposeOnClose: false))
                {
                    writer.GetCollection("rows").Insert(Row(1));
                    for (var attempt = 0; attempt < 10; attempt++) Assert.NotNull(reader.GetCollection("rows").FindById(1));
                    Assert.True(engine.CoordinatedReadHits > 0, "Precondition: the connection serves coordinated reads. " + engine.CoordinationFallbackReason);

                    var begin = Task.Run(() => reader.BeginTransaction());
                    Assert.True(begin.Wait(TimeSpan.FromSeconds(20)), "Begin on a coordinated connection did not complete.");
                    var tx = begin.Result;
                    Assert.True(Task.Run(() =>
                    {
                        tx.GetCollection("rows").Insert(Row(2));
                        tx.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 11 });
                        Assert.Equal(11, tx.GetCollection("rows").FindById(1)["value"].AsInt32);
                    }).Wait(TimeSpan.FromSeconds(20)));
                    // This connection's ordinary reads are served from its coordinated snapshot and
                    // do not see the uncommitted handle writes. (Other connections' reads wait for the
                    // writer mutex the handle holds until it completes, so none is attempted here.)
                    var read = Task.Run(() => reader.GetCollection("rows").FindById(1)["value"].AsInt32);
                    Assert.True(read.Wait(TimeSpan.FromSeconds(20)), "A coordinated read waited for the handle.");
                    Assert.Equal(10, read.Result);
                    Assert.True(Task.Run(() => tx.Commit()).Wait(TimeSpan.FromSeconds(20)));
                    Assert.Equal(LiteTransactionState.Committed, tx.State);
                    tx.Dispose();
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        Assert.Equal(new[] { 1, 2 }, Ids(reader));
                        Assert.Equal(11, reader.GetCollection("rows").FindById(1)["value"].AsInt32);
                    }
                    Assert.Equal(new[] { 1, 2 }, Ids(writer));
                    using (var rolledBack = reader.BeginTransaction())
                    {
                        rolledBack.GetCollection("rows").Insert(Row(3));
                        rolledBack.Rollback();
                    }
                    writer.GetCollection("rows").Insert(Row(4));
                    Assert.Equal(new[] { 1, 2, 4 }, Ids(reader));
                }
                for (var reopen = 0; reopen < 2; reopen++)
                {
                    using var cold = new LiteDatabase(file);
                    Assert.Equal(new[] { 1, 2, 4 }, Ids(cold));
                    Assert.Equal(11, cold.GetCollection("rows").FindById(1)["value"].AsInt32);
                }
            }
            finally { Directory.Delete(directory, true); }
        }
#endif
    }
}
