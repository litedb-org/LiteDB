using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Tests.Engine.TransactionHandleSharedCallback_Tests;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleSharedPinCallback_Tests
    {
        [Theory]
        [InlineData(null, false, false)] [InlineData("secret", false, false)]
        [InlineData(null, true, false)] [InlineData("secret", true, false)]
        [InlineData(null, false, true)] [InlineData("secret", false, true)]
        [InlineData(null, true, true)] [InlineData("secret", true, true)]
        public void Leased_anchor_does_not_allow_handle_callback_to_start_a_self_dependent_pin(
            string password, bool commit, bool facade)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var shared = Open(file, password))
            using (var db = new LiteDatabase(shared))
            using (var anchor = shared.Query("rows", new Query()))
            {
                AssertLeasedAnchor(shared, anchor);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(3));
                var nativeAttempts = 0;
                Turnstile(shared).BeforeMainWait = () =>
                {
                    Interlocked.Increment(ref nativeAttempts);
                    // The raw Shared API has no session cancellation token. Stop the
                    // regressed holder at its actual native acquisition boundary.
                    throw new OperationCanceledException("Test stopped callback-dependent pin acquisition.");
                };
                Exception refusal = null;
                IEnumerable<BsonDocument> Input()
                {
                    refusal = Record.Exception(() =>
                    {
                        if (facade) db.GetCollection("ordinary").Insert(Row(99));
                        else shared.Insert("ordinary", new[] { Row(99) }, BsonAutoId.Int32);
                    });
                    var acquired = Field<Mutex>(shared, "_mutex").WaitOne(0);
                    if (acquired) Field<Mutex>(shared, "_mutex").ReleaseMutex();
                    Assert.False(acquired); // The handle still excludes independent native owners.
                    yield return Row(4);
                }
                tx.GetCollection("rows").Insert(Input());
                Assert.IsType<InvalidOperationException>(refusal);
                Assert.Equal(0, nativeAttempts);
                Assert.Null(Field<SharedMutexPin>(shared, "_pin"));
                Assert.Equal(LiteTransactionState.Active, tx.State);
                Assert.NotNull(tx.GetCollection("rows").FindById(3));
                Assert.NotNull(tx.GetCollection("rows").FindById(4));
                if (commit) tx.Commit();
                else tx.Rollback();
                Turnstile(shared).BeforeMainWait = null;
                using var peer = new LiteDatabase(new SharedEngine(Settings(file, password)));
                PeerWrite(peer);
            }
            Verify(file, password, commit ? new[] { 1, 2, 3, 4, 5 } : new[] { 1, 2, 5 });
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Ordinary_write_with_leased_anchor_still_starts_and_releases_a_pin(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var shared = Open(file, password))
            using (var db = new LiteDatabase(shared))
            {
                using (var anchor = shared.Query("rows", new Query()))
                {
                    AssertLeasedAnchor(shared, anchor);
                    var nativeAttempts = 0;
                    Turnstile(shared).BeforeMainWait = () => Interlocked.Increment(ref nativeAttempts);
                    shared.Insert("rows", new[] { Row(3) }, BsonAutoId.Int32);
                    Assert.Equal(1, nativeAttempts);
                    AssertOwnedPin(shared);
                }
                Assert.Null(Field<SharedMutexPin>(shared, "_pin"));
                using var peer = new LiteDatabase(new SharedEngine(Settings(file, password)));
                PeerWrite(peer);
            }
            Verify(file, password, new[] { 1, 2, 3, 5 });
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Handle_callback_can_start_an_independent_other_database_pin(string password)
        {
            using var first = new TempFile();
            using var second = new TempFile();
            Seed(first, password);
            Seed(second, password);
            using (var owner = new LiteDatabase(new SharedEngine(Settings(first, password))))
            using (var independent = Open(second, password))
            using (var db = new LiteDatabase(independent))
            {
                using (var anchor = independent.Query("rows", new Query()))
                using (var tx = owner.BeginTransaction())
                {
                    AssertLeasedAnchor(independent, anchor);
                    IEnumerable<BsonDocument> Input()
                    {
                        independent.Insert("rows", new[] { Row(3) }, BsonAutoId.Int32);
                        AssertOwnedPin(independent);
                        yield return Row(3);
                    }
                    tx.GetCollection("rows").Insert(Input());
                    tx.Rollback();
                }
                Assert.Null(Field<SharedMutexPin>(independent, "_pin"));
                PeerWrite(owner);
                PeerWrite(db);
            }
            Verify(first, password, new[] { 1, 2, 5 });
            Verify(second, password, new[] { 1, 2, 3, 5 });
        }

        [Theory]
        [InlineData(null)] [InlineData("secret")]
        public void Leased_anchor_pin_start_may_wait_for_an_idle_handle_completed_elsewhere(string password)
        {
            using var file = new TempFile();
            Seed(file, password);
            using (var shared = Open(file, password))
            using (var db = new LiteDatabase(shared))
            using (var anchor = shared.Query("rows", new Query()))
            {
                AssertLeasedAnchor(shared, anchor);
                using var tx = db.BeginTransaction();
                tx.GetCollection("rows").Insert(Row(3));
                using var acquiring = new ManualResetEventSlim();
                using var completed = new ManualResetEventSlim();
                Exception failure = null;
                var completing = new Thread(() =>
                {
                    try { Assert.True(acquiring.Wait(TimeSpan.FromSeconds(5))); tx.Commit(); }
                    catch (Exception error) { failure = error; }
                    finally { completed.Set(); }
                }) { IsBackground = true };
                Turnstile(shared).BeforeMainWait = () =>
                {
                    acquiring.Set();
                    // Bound a broken handoff without allowing the pin holder to hang.
                    if (!completed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Idle handle did not complete.");
                };
                completing.Start();
                shared.Insert("rows", new[] { Row(4) }, BsonAutoId.Int32);
                Assert.True(completing.Join(TimeSpan.FromSeconds(5)));
                Assert.Null(failure);
                Assert.True(acquiring.IsSet);
                Assert.Equal(LiteTransactionState.Committed, tx.State);
                AssertOwnedPin(shared);
                Turnstile(shared).BeforeMainWait = null;
                anchor.Dispose();
                using var peer = new LiteDatabase(new SharedEngine(Settings(file, password)));
                PeerWrite(peer);
            }
            Verify(file, password, new[] { 1, 2, 3, 4, 5 });
        }

        private static SharedEngine Open(string file, string password) =>
            new SharedEngine(new EngineSettings
            {
                Filename = file, Password = password,
                ReadTransform = (_, value) => value
            }) { PinIdleLimit = TimeSpan.FromMinutes(1), PinHoldLimit = TimeSpan.FromMinutes(1) };

        private static T Field<T>(object target, string name) => (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void AssertLeasedAnchor(SharedEngine shared, IBsonDataReader anchor)
        {
            Assert.IsType<SharedDataReader>(anchor);
            Assert.True(anchor.Read());
            Assert.Equal(1, anchor.Current["_id"].AsInt32);
            var readers = Field<Dictionary<int, int>>(shared, "_localReaders");
            Assert.Equal(1, readers[Environment.CurrentManagedThreadId]);
            Assert.Null(Field<SharedMutexPin>(shared, "_pin"));
            Assert.False(shared.MutexOwner.IsOwnedByCurrentThread);
        }

        private static void AssertOwnedPin(SharedEngine shared)
        {
            var pin = Field<SharedMutexPin>(shared, "_pin");
            Assert.NotNull(pin);
            Assert.Same(Thread.CurrentThread, pin.Owner);
            Assert.True(pin.Counted);
        }
    }
}
