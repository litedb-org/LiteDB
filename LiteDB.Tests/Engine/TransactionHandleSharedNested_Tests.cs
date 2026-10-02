using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Characterization (#3073): a begin through one connection, on a thread that retains another
    /// connection's ownership of the same file, waits like an ordinary write does; disposing that
    /// other connection from another thread lets it proceed.
    /// </summary>
    public class TransactionHandleSharedNested_Tests
    {
        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };

#pragma warning disable CS0618
        [Fact]
        public void Begin_through_a_peer_connection_waits_for_the_same_threads_legacy_owner()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file)) seed.GetCollection("rows").Insert(Row(1));
            var shared = new ConnectionString { Filename = file, Connection = ConnectionType.Shared };
            using var a = new LiteDatabase(shared);
            var b = new LiteDatabase(shared);
            using var retained = new ManualResetEventSlim();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    b.BeginTrans();
                    b.GetCollection("rows").Insert(Row(2));
                    retained.Set();
                    // This thread retains B's writer ownership; A's begin cannot be refused here.
                    using var tx = a.BeginTransaction();
                    tx.GetCollection("rows").Insert(Row(3));
                    tx.Commit();
                }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            thread.Start();
            Assert.True(retained.Wait(TimeSpan.FromSeconds(10)));
            var engine = (SharedEngine)typeof(LiteDatabase).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(a);
            var turnstile = TransactionHandleSharedCallback_Tests.Turnstile(engine);
            Assert.True(SpinWait.SpinUntil(turnstile.HasWaiter, TimeSpan.FromSeconds(10)));
            Assert.True(thread.IsAlive, "The begin did not wait for the same thread's peer ownership.");
            // The way out: dispose the retaining connection from another thread.
            var closer = new Thread(() => b.Dispose());
            closer.Start();
            Assert.True(closer.Join(TimeSpan.FromSeconds(20)));
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The begin did not proceed after the peer closed.");
            Assert.Null(failure);
            using var cold = new LiteDatabase(file);
            Assert.Equal(new[] { 1, 3 }, cold.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x));
        }
#pragma warning restore CS0618
    }
}
