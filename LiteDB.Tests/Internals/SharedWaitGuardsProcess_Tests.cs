#if !NETFRAMEWORK
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    /// <summary>
    /// SharedWriterTimeout against an owner in another process (#3080): a timed-out wait has no
    /// side effect and leaves nothing owned, and an owner killed while a bounded wait waits for it
    /// is acquired from (the OS abandons its mutex) and recovered, never reported as a timeout.
    /// </summary>
    public class SharedWaitGuardsProcess_Tests
    {
        public enum Owner { Legacy, Handle, Turnstile }

        public enum Path { Scoped, Holder, Pin, Begin }

        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan Late = TimeSpan.FromMilliseconds(250);
        private static readonly Path[] Paths = { Path.Scoped, Path.Holder, Path.Begin, Path.Pin };

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = 1000 + id };

        [Theory]
        [InlineData(Owner.Legacy, null)] [InlineData(Owner.Legacy, "secret")]
        [InlineData(Owner.Handle, null)] [InlineData(Owner.Handle, "secret")]
        [InlineData(Owner.Turnstile, null)]
        public async Task Writer_wait_times_out_against_another_process(Owner owner, string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using var worker = new Worker();
            // One connection per path; all their calls run on the worker thread, which also streams
            // the pin path's leased reader (taken before the other process owns the database).
            var waiters = await worker.Run(() => Paths.Select(path => new Waiter(file, password, path, Budget)).ToArray());
            try
            {
                await worker.Run(() =>
                {
                    // Uncontended first, so each timed wait below measures the wait alone.
                    for (var i = 0; i < waiters.Length; i++) waiters[i].Write(10 + i);
                    waiters.Single(waiter => waiter.Path == Path.Pin).OpenAnchor();
                });
                using (var child = new MvccProcess(owner == Owner.Legacy ? "legacy-hold" : owner == Owner.Handle ? "handle-hold" : "turnstile-hold",
                    file, password, "shared"))
                {
                    await child.Expect("ready");
                    for (var i = 0; i < waiters.Length; i++)
                    {
                        var waiter = waiters[i];
                        var id = 20 + i;
                        var attempt = worker.Run(() =>
                        {
                            var elapsed = Stopwatch.StartNew();
                            var error = Record.Exception(() => waiter.Write(id));
                            return (error, elapsed.Elapsed);
                        });
                        // A wait that ignored its budget would wait for the child: killing it (on dispose) ends that wait.
                        var finished = await Task.WhenAny(attempt, Task.Delay(Budget + TimeSpan.FromSeconds(10)));
                        Assert.True(finished == attempt, $"{waiter.Path}: the wait did not end within its budget.");
                        var (failure, took) = await attempt;
                        var timeout = Assert.IsType<LiteException>(failure);
                        Assert.Equal(LiteException.LOCK_TIMEOUT, timeout.ErrorCode);
                        Assert.Contains("The owner is another connection or process.", timeout.Message);
                        Assert.True(took >= Budget - TimeSpan.FromMilliseconds(50) && took <= Budget + Late, $"{waiter.Path}: timed out after {took}");
                        var diagnostics = waiter.Db.GetSharedWaitDiagnostics();
                        Assert.Equal((1L, 0), (diagnostics.Total.TimedOut, diagnostics.CurrentWaiters));
                    }
                    // The other process still owns what it held, and ends it as it chooses.
                    child.Send(owner == Owner.Turnstile ? "release" : "commit");
                    await child.Expect("done");
                    await child.Finish(release: owner == Owner.Handle);
                }
                await worker.Run(() =>
                {
                    // Every connection writes again; the pin path last, through a pin of its reader.
                    for (var i = 0; i < waiters.Length; i++) waiters[i].Write(30 + i);
                    var pin = waiters.Single(waiter => waiter.Path == Path.Pin);
                    Assert.NotNull(pin.Engine.Pin);
                    pin.CloseAnchor();
                });
            }
            finally { await worker.Run(() => { foreach (var waiter in waiters) waiter.Dispose(); }); }
            var expected = new[] { 1 }.Concat(owner == Owner.Turnstile ? new int[0] : new[] { 2 })
                .Concat(Enumerable.Range(10, Paths.Length)).Concat(Enumerable.Range(30, Paths.Length)).ToArray();
            Verify(file, password, expected);
            Verify(file, password, expected);
        }

        [Theory]
        [InlineData(Owner.Legacy, Path.Scoped, null)] [InlineData(Owner.Legacy, Path.Holder, "secret")]
        [InlineData(Owner.Handle, Path.Holder, null)] [InlineData(Owner.Handle, Path.Scoped, "secret")]
        public async Task Owner_killed_while_waiting_with_a_budget_is_acquired_not_timed_out(Owner owner, Path path, string password)
        {
            var budget = TimeSpan.FromSeconds(10);
            using var file = new TempFile();
            Seed(file, password);
            using var worker = new Worker();
            var waiter = await worker.Run(() => new Waiter(file, password, path, budget));
            try
            {
                Task<(Exception, TimeSpan)> attempt;
                // The child's uncommitted id 2 spilled to the WAL; it owns the native writer mutex.
                using (var child = new MvccProcess(owner == Owner.Legacy ? "legacy-hold" : "handle-hold", file, password, "shared"))
                {
                    await child.Expect("ready");
                    attempt = worker.Run(() =>
                    {
                        var elapsed = Stopwatch.StartNew();
                        var error = Record.Exception(() => waiter.Write(3));
                        return (error, elapsed.Elapsed);
                    });
                    Assert.True(SpinWait.SpinUntil(() => waiter.Db.GetSharedWaitDiagnostics().CurrentWaiters == 1, TimeSpan.FromSeconds(10)),
                        "The write never waited for the other process.");
                    Assert.False(attempt.IsCompleted);
                    await child.Kill();
                }
                var finished = await Task.WhenAny(attempt, Task.Delay(budget + TimeSpan.FromSeconds(10)));
                Assert.True(finished == attempt, "The write did not end.");
                var (failure, took) = await attempt;
                Assert.Null(failure);
                Assert.True(took < budget, $"Acquired after {took}");
                var diagnostics = waiter.Db.GetSharedWaitDiagnostics();
                Assert.Equal((1L, 0L, 0), (diagnostics.Total.Count, diagnostics.Total.TimedOut, diagnostics.CurrentWaiters));
                await worker.Run(() =>
                {
                    // The killed owner's uncommitted insert is gone; the connection keeps writing.
                    Assert.Null(waiter.Db.GetCollection("rows").FindById(2));
                    waiter.Write(4);
                });
            }
            finally { await worker.Run(waiter.Dispose); }
            Verify(file, password, new[] { 1, 3, 4 });
            Verify(file, password, new[] { 1, 3, 4 });
        }

        private static void Seed(string file, string password)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.GetCollection("rows").EnsureIndex("value", true);
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
        }

        // A cold Direct reopen holds exactly these rows, and the unique index agrees with them.
        private static void Verify(string file, string password, int[] ids)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            Assert.Equal(ids, db.GetCollection("rows").FindAll().Select(row => row["_id"].AsInt32).OrderBy(id => id));
            Assert.Equal(ids.Length, db.GetCollection("rows").Find(Query.GTE("value", 42)).Count());
            Assert.NotNull(db.GetCollection("sentinel").FindById(9));
        }

        /// <summary>A Shared connection whose writes take one acquisition path.</summary>
        private sealed class Waiter : IDisposable
        {
            internal readonly Path Path;
            internal readonly SharedEngine Engine;
            internal readonly LiteDatabase Db;
            private IBsonDataReader _anchor;

            internal Waiter(string file, string password, Path path, TimeSpan timeout)
            {
                Path = path;
                var settings = new EngineSettings { Filename = file, Password = password, SharedWriterTimeout = timeout };
                // A read callback keeps queries off mapped snapshots: the pin path's reader is leased.
                if (path == Path.Pin) settings.ReadTransform = (_, value) => value;
                Engine = new SharedEngine(settings);
                Db = new LiteDatabase(Engine);
            }

            // An array input acquires on the calling thread (scoped); a collection insert through the
            // connection's holder thread, or through a pin while this thread streams a leased reader;
            // a begin through the handle's holder thread.
            internal void Write(int id)
            {
                if (Path == Path.Scoped) Engine.Insert("rows", new[] { Row(id) }, BsonAutoId.Int32);
                else if (Path != Path.Begin) Db.GetCollection("rows").Insert(Row(id));
                else
                {
                    using var tx = Db.BeginTransaction();
                    tx.GetCollection("rows").Insert(Row(id));
                    tx.Commit();
                }
            }

            internal void OpenAnchor()
            {
                _anchor = Engine.Query("sentinel", new Query());
                Assert.True(_anchor.Read());
            }

            internal void CloseAnchor()
            {
                _anchor?.Dispose();
                _anchor = null;
            }

            public void Dispose()
            {
                this.CloseAnchor();
                Db.Dispose();
            }
        }

        /// <summary>One dedicated thread running the parent's calls in order (a pin is per thread).</summary>
        private sealed class Worker : IDisposable
        {
            private readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();
            private readonly Thread _thread;

            internal Worker()
            {
                _thread = new Thread(() => { foreach (var work in _work.GetConsumingEnumerable()) work(); }) { IsBackground = true, Name = "test waiter" };
                _thread.Start();
            }

            internal Task<T> Run<T>(Func<T> work)
            {
                var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _work.Add(() =>
                {
                    try { done.SetResult(work()); }
                    catch (Exception error) { done.SetException(error); }
                });
                return done.Task;
            }

            internal Task Run(Action work) => this.Run(() => { work(); return true; });

            public void Dispose()
            {
                _work.CompleteAdding();
                _thread.Join(TimeSpan.FromSeconds(20));
                _work.Dispose();
            }
        }
    }
}
#endif
