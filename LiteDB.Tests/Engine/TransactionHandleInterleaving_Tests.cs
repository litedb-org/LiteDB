using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleInterleaving_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Forced_histories_preserve_independent_handles_and_reopen(bool encrypted, bool commit)
        {
            using var file = new TempFile();
            var settings = new EngineSettings { Filename = file.Filename, Password = encrypted ? "secret" : null };
            using (var engine = new LiteEngine(settings))
            {
                engine.Insert("rows", new[] { TransactionHandleProcessHost_Tests.Row(1) }, BsonAutoId.Int32);
                engine.EnsureIndex("rows", "value", "$.value", true);
                var first = engine.BeginHandleTransaction();
                var second = engine.BeginHandleTransaction();
                first.Invoke(e => e.Insert("rows", new[] { TransactionHandleProcessHost_Tests.Row(2) }, BsonAutoId.Int32));
                using var attempted = new ManualResetEventSlim();
                var locker = typeof(LiteEngine).GetField("_locker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(engine);
                var locks = (ConcurrentDictionary<string, CollectionLock>)locker.GetType().GetField("_collections", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(locker);
                locks["rows"].BeforeWait = () => attempted.Set();
                var waiter = Task.Run(() =>
                {
                    return second.Invoke(e => e.Insert("rows", new[] { TransactionHandleProcessHost_Tests.Row(3) }, BsonAutoId.Int32));
                });
                Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(waiter.IsCompleted);
                // The first handle keeps the collection lock across sequential thread handoff.
                Assert.True(Task.Run(() => first.Invoke(e => commit ? e.Commit() : e.Rollback())).GetAwaiter().GetResult());
                Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)));
                Assert.Equal(1, waiter.Result);
                Assert.True(second.Invoke(e => e.Commit()));
            }
            for (var open = 0; open < 2; open++)
            {
                using var engine = new LiteEngine(settings);
                Assert.Equal(commit ? 3 : 2, TransactionHandleProcessHost_Tests.Count(engine, "rows"));
                using var reader = engine.Query("rows", new Query { Where = { BsonExpression.Create("value >= 1") } });
                var ids = new List<int>();
                while (reader.Read()) ids.Add(reader.Current["_id"].AsInt32);
                ids.Sort();
                Assert.Equal(commit ? new[] { 1, 2, 3 } : new[] { 1, 3 }, ids);
            }
        }
    }
}
