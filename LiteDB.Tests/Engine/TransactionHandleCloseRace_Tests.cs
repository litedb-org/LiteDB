using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// Database close racing handles that hop threads mid-transaction: close always returns, no
    /// handle stays active or indeterminate, every acknowledged commit survives cold reopens,
    /// nothing uncommitted appears, and the index agrees with the data. Handles only: close
    /// races with in-flight ordinary or legacy writes are a pre-existing, separate concern.
    /// </summary>
    public class TransactionHandleCloseRace_Tests
    {
        private static readonly int Rounds = int.TryParse(Environment.GetEnvironmentVariable("LITEDB_HANDLE_CLOSE_ROUNDS"), out var rounds) ? rounds : 12;

        private static BsonDocument Row(long id) => new BsonDocument { ["_id"] = id, ["value"] = (int)(((id % 7) + 7) % 7) };

        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "secret")]
        [InlineData(true, null)]
        [InlineData(true, "secret")]
        public void Close_racing_handles_keeps_exactly_the_acknowledged_commits(bool shared, string password)
        {
            for (var round = 0; round < Rounds; round++)
            {
                using var file = new TempFile();
                var settings = new ConnectionString { Filename = file, Password = password,
                    Connection = shared ? ConnectionType.Shared : ConnectionType.Direct };
                using (var seed = new LiteDatabase(settings))
                {
                    seed.GetCollection("rows").EnsureIndex("value");
                    seed.GetCollection("rows").Insert(Row(0));
                }
                var acked = new ConcurrentDictionary<long, bool>();
                var handles = new ConcurrentQueue<ILiteTransaction>();
                var unexpected = new ConcurrentQueue<Exception>();
                var db = new LiteDatabase(settings);
                var next = 0L;
                var random = new Random(round);
                var workers = Enumerable.Range(0, 3).Select(worker => Task.Run(async () =>
                {
                    while (true)
                    {
                        ILiteTransaction tx;
                        try { tx = db.BeginTransaction(); }
                        catch (ObjectDisposedException) { return; }
                        handles.Enqueue(tx);
                        var ids = new List<long>();
                        try
                        {
                            for (var i = 0; i < 1 + worker; i++)
                            {
                                var id = Interlocked.Increment(ref next);
                                await Task.Yield();
                                tx.GetCollection("rows").Insert(Row(id));
                                ids.Add(id);
                            }
                            await Task.Yield();
                            tx.Commit();
                            foreach (var id in ids) acked[id] = true;
                        }
                        catch (ObjectDisposedException) { return; }
                        catch (Exception error) { unexpected.Enqueue(error); return; }
                    }
                })).ToArray();
                Thread.Sleep(random.Next(5, 40));
                var close = Task.Run(() => db.Dispose());
                Assert.True(close.Wait(TimeSpan.FromSeconds(30)), "Close did not return.");
                close.GetAwaiter().GetResult();
                Assert.True(Task.WaitAll(workers, TimeSpan.FromSeconds(30)));
                Assert.Empty(unexpected);
                Assert.All(handles, tx => Assert.True(tx.State == LiteTransactionState.Committed || tx.State == LiteTransactionState.RolledBack, tx.State.ToString()));
                var expected = acked.Keys.Concat(new[] { 0L }).OrderBy(x => x).ToArray();
                for (var reopen = 0; reopen < 2; reopen++)
                {
                    using var cold = new LiteDatabase(settings);
                    var rows = cold.GetCollection("rows");
                    Assert.Equal(expected, rows.FindAll().Select(x => x["_id"].AsInt64).OrderBy(x => x));
                    Assert.Equal(expected.Length, Enumerable.Range(0, 7).Sum(v => rows.Count(Query.EQ("value", v))));
                    if (reopen == 0) rows.Insert(Row(-1 - round));
                    expected = expected.Concat(new[] { -1L - round }).OrderBy(x => x).ToArray();
                }
            }
        }
    }
}
