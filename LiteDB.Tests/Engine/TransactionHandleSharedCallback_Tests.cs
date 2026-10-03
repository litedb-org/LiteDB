using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleSharedCallback_Tests
    {
        [Theory]
        [InlineData(null, false, false)] [InlineData("secret", false, false)]
        [InlineData(null, true, false)] [InlineData("secret", true, false)]
        [InlineData(null, false, true)] [InlineData("secret", false, true)]
        [InlineData(null, true, true)] [InlineData("secret", true, true)]
        public void Input_callback_refuses_same_namespace_before_native_wait(string password, bool write, bool peerFacade)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var shared = new SharedEngine(Settings(file, password)))
            using (var db = new LiteDatabase(shared))
            using (var peerEngine = new SharedEngine(Settings(Path.Combine(Path.GetDirectoryName(file.Filename), ".", Path.GetFileName(file.Filename)), password)))
            using (var peer = new LiteDatabase(peerEngine))
            using (var tx = db.BeginTransaction())
            {
                var target = peerFacade ? peer : db;
                var waited = false;
                Turnstile(peerFacade ? peerEngine : shared).BeforeMainWait = () =>
                {
                    waited = true;
                    // Make an actual regression fail at the native wait, without hanging the host.
                    throw new OperationCanceledException("Test stopped a self-dependent native wait.");
                };
                tx.GetCollection("rows").Insert(Row(3));
                Exception refusal = null;
                IEnumerable<BsonDocument> Input()
                {
                    refusal = Record.Exception(() =>
                    {
                        if (write) target.GetCollection("ordinary").Insert(Row(99));
                        else Assert.Equal(0, target.UserVersion);
                    });
                    yield return Row(4);
                }
                tx.GetCollection("rows").Insert(Input());
                Assert.IsType<InvalidOperationException>(refusal);
                Assert.False(waited);
                // The tripwire covers the callback only; later waits are legitimate.
                Turnstile(peerFacade ? peerEngine : shared).BeforeMainWait = null;
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.NotNull(tx.GetCollection("rows").FindById(3));
                Assert.NotNull(tx.GetCollection("rows").FindById(4));
                if (write) tx.Rollback();
                else tx.Commit();
                PeerWrite(peer);
            }
            Verify(file, password, write ? new[] { 1, 2, 5 } : new[] { 1, 2, 3, 4, 5 });
        }

        [Theory]
        [InlineData(null, false)] [InlineData("secret", false)]
        [InlineData(null, true)] [InlineData("secret", true)]
        public void Late_reader_callback_refuses_native_wait_after_handoff(string password, bool write)
        {
            using var file = new TempFile();
            Seed(file, password);
            Action callback = null;
            var settings = Settings(file, password);
            settings.ReadTransform = (_, value) =>
            {
                if (value.IsDocument && value["_id"] == 2) callback?.Invoke();
                return value;
            };
            using (var shared = new SharedEngine(settings))
            using (var db = new LiteDatabase(shared))
            using (var tx = db.BeginTransaction())
            {
                tx.GetCollection("rows").Insert(Row(3));
                var waited = false;
                Turnstile(shared).BeforeMainWait = () => { waited = true; throw new OperationCanceledException("native self-wait"); };
                Exception refusal = null;
                using (var reader = tx.GetCollection("rows").FindAll().GetEnumerator())
                {
                    Assert.True(reader.MoveNext());
                    Assert.Equal(1, reader.Current["_id"].AsInt32);
                    var calls = 0;
                    callback = () =>
                    {
                        calls++;
                        refusal = Record.Exception(() =>
                        {
                            if (write) db.GetCollection("ordinary").Insert(Row(99));
                            else Assert.Equal(0, db.UserVersion);
                        });
                    };
                    OnThread(() => Assert.True(reader.MoveNext()));
                    Assert.Equal(2, reader.Current["_id"].AsInt32);
                    Assert.Equal(1, calls);
                    callback = null;
                }
                Assert.IsType<InvalidOperationException>(refusal);
                Assert.False(waited);
                Turnstile(shared).BeforeMainWait = null;
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.NotNull(tx.GetCollection("rows").FindById(3));
                OnThread(tx.Commit);
                using var peer = new LiteDatabase(new SharedEngine(Settings(file, password)));
                PeerWrite(peer);
            }
            Verify(file, password, new[] { 1, 2, 3, 5 });
        }

        internal static EngineSettings Settings(string file, string password) =>
            new EngineSettings { Filename = file, Password = password };
        internal static SharedMutexTurnstile Turnstile(SharedEngine engine) =>
            (SharedMutexTurnstile)typeof(SharedEngine).GetField("_turnstile", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
        internal static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id * 10 };
        internal static void OnThread(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
        }
        internal static void PeerWrite(LiteDatabase peer) => OnThread(() => peer.GetCollection("rows").Insert(Row(5)));
        internal static void Seed(string file, string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Insert(new[] { Row(1), Row(2) });
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9, ["value"] = "untouched" });
            db.Checkpoint();
        }
        internal static void Verify(string file, string password, int[] expected)
        {
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
                var query = db.GetCollection("rows").Query().Where(Query.GTE("value", 0));
                Assert.Equal("value", query.GetPlan()["index"]["name"].AsString);
                Assert.Equal(expected, query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                Assert.Equal(expected.Length, db.GetCollection("rows").Count());
                foreach (var id in expected) Assert.Equal(id * 10, db.GetCollection("rows").FindById(id)["value"].AsInt32);
                Assert.Equal(0, db.GetCollection("ordinary").Count());
                Assert.Equal("untouched", db.GetCollection("sentinel").FindById(9)["value"].AsString);
            }
        }
    }
}
