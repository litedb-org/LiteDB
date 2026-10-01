using System;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Threading;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleBoundDisposeRace_Tests
    {
        [Theory]
        [InlineData(false, false, null)]
        [InlineData(false, true, null)]
        [InlineData(true, false, null)]
        [InlineData(true, true, null)]
        [InlineData(false, false, "secret")]
        [InlineData(false, true, "secret")]
        [InlineData(true, false, "secret")]
        [InlineData(true, true, "secret")]
        public void Disposal_between_child_capture_and_admission_does_not_abort_writes(bool shared, bool enumerator, string password)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct, Password = password };
            using (var db = new LiteDatabase(settings))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                db.GetCollection("rows").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                using var tx = db.BeginTransaction();
                var rows = tx.GetCollection("rows");
                rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20 });
                var reader = rows.Query().ExecuteReader();
                var iterator = rows.FindAll().GetEnumerator();
                Assert.True(reader.Read());
                Assert.True(iterator.MoveNext());
                var gate = typeof(LiteTransaction).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tx);
                using var starting = new ManualResetEventSlim();
                Exception failure = null;
                var worker = new Thread(() =>
                {
                    starting.Set();
                    try { if (enumerator) iterator.MoveNext(); else reader.Read(); }
                    catch (Exception error) { failure = error; }
                }) { IsBackground = true };
                lock (gate)
                {
                    worker.Start();
                    Assert.True(starting.Wait(TimeSpan.FromSeconds(5)));
                    // This worker has no other wait: it has captured the child and is
                    // blocked in handle admission while this thread owns the gate.
                    Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)));
                    if (enumerator) iterator.Dispose(); else reader.Dispose();
                }
                Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
                Assert.IsType<ObjectDisposedException>(failure);
                Assert.Equal(LiteTransactionState.Active, tx.State);
                reader.Dispose();
                iterator.Dispose();
                tx.Commit();
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(settings);
                var query = db.GetCollection("rows").Query().Where(Query.GTE("value", 10));
                Assert.Equal("value", query.GetPlan()["index"]["name"].AsString);
                Assert.Equal(new[] { 1, 2 }, query.ToArray().Select(row => row["_id"].AsInt32).OrderBy(id => id));
                Assert.Equal(2, db.GetCollection("rows").Count());
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Actual_reader_operation_and_disposal_failures_still_abort(bool shared, bool disposal)
        {
            using var file = new TempFile();
            var settings = new ConnectionString { Filename = file,
                Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
            using (var db = new LiteDatabase(settings))
            {
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 10 });
                db.GetCollection("rows").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 20 });
                Exception original = disposal ? (Exception)new IOException("actual reader disposal failure") :
                    new ObjectDisposedException("actual reader failure");
                var reader = new GuardedTransactionReader((LiteTransaction)tx, new FailingReader(original));
                if (disposal) Assert.Same(original, Record.Exception(reader.Dispose));
                else Assert.Same(original, Record.Exception(() => reader.Read()));
                Assert.Equal(LiteTransactionState.Failed, tx.State);
                reader.Dispose();
            }
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(settings);
                var query = db.GetCollection("rows").Query().Where(Query.EQ("value", 10));
                Assert.Equal("value", query.GetPlan()["index"]["name"].AsString);
                Assert.StartsWith("INDEX SEEK", query.GetPlan()["index"]["mode"].AsString);
                Assert.Equal(new[] { 1 }, query.ToArray().Select(row => row["_id"].AsInt32));
                Assert.Equal(1, db.GetCollection("rows").Count());
                Assert.NotNull(db.GetCollection("sentinel").FindById(9));
            }
        }

        private sealed class FailingReader : IBsonDataReader
        {
            private readonly Exception _failure;
            internal FailingReader(Exception failure) { _failure = failure; }
            public BsonValue this[string field] => throw _failure;
            public string Collection => "rows";
            public BsonValue Current => throw _failure;
            public bool HasValues => true;
            public bool Read() => throw _failure;
            public void Dispose() => throw _failure;
        }
    }
}
