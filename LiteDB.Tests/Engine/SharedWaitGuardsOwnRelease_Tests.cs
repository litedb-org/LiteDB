using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;
using static LiteDB.Tests.Engine.SharedWaitGuards_Tests;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// This connection's own release in flight (a posted release, a pin closing) is waited for
    /// first and not charged to SharedWriterTimeout; an exited owner's cleanup is waited for only
    /// within the budget.
    /// </summary>
    public class SharedWaitGuardsOwnRelease_Tests
    {
        private readonly ITestOutputHelper _out;
        public SharedWaitGuardsOwnRelease_Tests(ITestOutputHelper output) { _out = output; }
        private void Trace(string line) => _out.WriteLine(line);

#pragma warning disable CS0618
        /// <summary>
        /// A bounded waiter that arrives after the holder's poll claimed an exited owner blocks in
        /// WaitForRelease for the whole exited-owner cleanup, uncharged: the budget is overrun.
        /// </summary>
        [Fact]
        public void A_bounded_wait_arriving_during_exited_owner_cleanup_stays_within_its_budget()
        {
            using var file = new TempFile();
            using var cleaning = new ManualResetEventSlim();
            using var proceed = new ManualResetEventSlim();
            var settings = new EngineSettings { Filename = file, SharedWriterTimeout = TimeSpan.FromMilliseconds(500) };
            using var engine = new SharedEngine(settings);
            using var database = new LiteDatabase(engine);
            database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 0 });
            engine.MutexOwner.BeforeOwnerExitedCleanup = () => { cleaning.Set(); proceed.Wait(TimeSpan.FromSeconds(8)); };
            var owner = new Thread(() =>
            {
                database.BeginTrans();
                database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            });
            owner.Start();
            Assert.True(owner.Join(TimeSpan.FromSeconds(10)));
            // No waiter exists: the holder's own poll claims the exited owner and runs its cleanup.
            Assert.True(cleaning.Wait(TimeSpan.FromSeconds(10)), "The exited owner was never cleaned up.");
            Exception waitError = null;
            var watch = new Stopwatch();
            var waiter = new Thread(() =>
            {
                watch.Start();
                waitError = Record.Exception(() => database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 }));
                watch.Stop();
            });
            waiter.Start();
            bool returned;
            try { returned = waiter.Join(TimeSpan.FromSeconds(3)); }
            finally { proceed.Set(); waiter.Join(TimeSpan.FromSeconds(10)); engine.MutexOwner.BeforeOwnerExitedCleanup = null; }
            Trace($"waiter elapsed {watch.Elapsed}, error {waitError?.GetType().Name}: {waitError?.Message}");
            Assert.True(returned, $"A 500 ms budgeted wait was still blocked after 3 s (total {watch.Elapsed}).");
        }
#pragma warning restore CS0618

        /// <summary>
        /// Docs: this connection's own release of its previous call is waited for first and not
        /// charged, so a zero budget does not fail on an uncontended connection. BeginTransaction's
        /// child does not wait for the parent connection's posted release.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Zero_budget_begin_after_this_connections_own_release_does_not_time_out(bool slowRelease)
        {
            using var file = new TempFile();
            Seed(file);
            var settings = Settings(file, timeout: TimeSpan.Zero);
            // Keeps ordinary writes on the holder thread, whose release is posted asynchronously.
            settings.ReadTransform = (_, value) => value;
            var failures = new List<string>();
            var rounds = slowRelease ? 3 : 200;
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine))
            {
                if (slowRelease) engine.MutexOwner.BeforePostedRelease = () => Thread.Sleep(150);
                for (var i = 0; i < rounds; i++)
                {
                    db.GetCollection("rows").Insert(Row(100 + i));
                    var error = Record.Exception(() =>
                    {
                        using var tx = db.BeginTransaction();
                        tx.Commit();
                    });
                    if (error != null) failures.Add(error.GetType().Name + ": " + error.Message);
                }
                engine.MutexOwner.BeforePostedRelease = null;
            }
            Trace($"{failures.Count}/{rounds} failed; first: {failures.FirstOrDefault()}");
            Assert.True(failures.Count == 0, $"{failures.Count} of {rounds} begins failed; first: {failures.FirstOrDefault()}");
        }

        /// <summary>
        /// A thread streaming a leased reader writes through pins. When a pin reaches its hold limit
        /// the next write starts a new pin, whose holder must wait for this connection's previous pin
        /// to close the engine and release. That is this connection's own release, yet a zero budget
        /// charges it.
        /// </summary>
        [Fact]
        public void Zero_budget_writes_under_a_leased_reader_do_not_time_out_on_repin()
        {
            using var file = new TempFile();
            Seed(file);
            var settings = Settings(file, timeout: TimeSpan.Zero);
            settings.ReadTransform = (_, value) => value;
            var failures = new List<string>();
            var writes = 0;
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine))
            {
                using var anchor = engine.Query("sentinel", new Query());
                Assert.True(anchor.Read());
                var watch = Stopwatch.StartNew();
                var id = 1000;
                while (watch.Elapsed < TimeSpan.FromMilliseconds(1500))
                {
                    writes++;
                    var error = Record.Exception(() => db.GetCollection("rows").Insert(Row(id++)));
                    if (error != null) failures.Add(error.GetType().Name + ": " + error.Message);
                }
                var d = db.GetSharedWaitDiagnostics();
                Trace($"writes {writes}, failures {failures.Count}, timedOut {d.Total.TimedOut}; first: {failures.FirstOrDefault()}");
            }
            Assert.True(failures.Count == 0, $"{failures.Count} of {writes} writes failed; first: {failures.FirstOrDefault()}");
        }
    }
}
