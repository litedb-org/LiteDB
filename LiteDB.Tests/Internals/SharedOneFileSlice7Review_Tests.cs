#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileSlice7Review_Tests
    {
        [OneFileFact]
        public void Implicit_abort_returns_ownership_before_the_original_facade_retries()
        {
            using var file = new MappedTestFile();
            Seed(file);
            var settings = new EngineSettings { Filename = file, SharedOneFile = true };
            using (var first = new SharedEngine(settings))
            using (var second = new SharedEngine(settings))
            using (var owner = new LiteDatabase(first, disposeOnClose: false))
            using (var peer = new LiteDatabase(second, disposeOnClose: false))
            {
                owner.BeginTrans().Should().BeTrue();
                owner.GetCollection("docs").Insert(Row(2));
                var abortedCore = Core(first);
                Action duplicate = () => owner.GetCollection("docs").Insert(Row(1));
                duplicate.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(110);
                abortedCore.HasActiveTransaction.Should().BeFalse("the inner engine already rolled back");

                Exception peerFailure;
                try
                {
                    peerFailure = Record.Exception(() =>
                    {
                        peer.BeginTrans().Should().BeTrue();
                        peer.GetCollection("docs").Insert(Row(3));
                        peer.Commit().Should().BeTrue();
                    });
                }
                finally { owner.Rollback(); }

                peerFailure.Should().BeNull("an aborted transaction must release its participant lease at the failing call");
            }
            Verify(file, new[] { 1, 3 });
        }

        [OneFileFact]
        public void Cold_peer_read_refuses_a_same_thread_foreign_transaction_before_native_wait()
        {
            using var file = new MappedTestFile();
            Seed(file);
            var settings = new EngineSettings { Filename = file, SharedOneFile = true };
            using var first = new SharedEngine(settings);
            using var second = new SharedEngine(settings);
            using var owner = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new LiteDatabase(second, disposeOnClose: false);
            using var attempted = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                failure = Record.Exception(() =>
                {
                    owner.BeginTrans().Should().BeTrue();
                    owner.GetCollection("docs").Insert(Row(2));
                    attempted.Set();
                    peer.GetCollection("docs").FindAll().ToArray();
                });
                finished.Set();
            }) { IsBackground = true };
            thread.Start();
            bool reachedNativeWait;
            try
            {
                attempted.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                SpinWait.SpinUntil(() => finished.IsSet || Field<int>(second, "_mutexWaiters") > 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue("the query must either refuse or reach its blocking acquisition");
                reachedNativeWait = !finished.IsSet;
            }
            finally
            {
                // Release the contested native ownership even on the broken revision.
                first.Dispose();
                thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            reachedNativeWait.Should().BeFalse("the caller cannot complete its own transaction while blocked inside the sibling read");
            failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("connection");
        }

        [OneFileFact]
        public void Pinned_owner_thread_exit_wakes_an_already_queued_local_transaction()
        {
            using var file = new MappedTestFile();
            Seed(file, 150);
            var settings = new EngineSettings { Filename = file, SharedOneFile = true };
            using var first = new SharedEngine(settings);
            using var second = new SharedEngine(settings);
            using var owner = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new LiteDatabase(second, disposeOnClose: false);
            using var owned = new ManualResetEventSlim();
            using var exitOwner = new ManualResetEventSlim();
            using var attempting = new ManualResetEventSlim();
            IBsonDataReader anchor = null;
            SharedMutexPin pin = null;
            Exception ownerFailure = null, peerFailure = null;
            var ownerThread = new Thread(() =>
            {
                ownerFailure = Record.Exception(() =>
                {
                    first.PinIdleLimit = first.PinHoldLimit = TimeSpan.FromMinutes(10);
                    anchor = first.Query("docs", new Query());
                    anchor.Read().Should().BeTrue();
                    first.Update("docs", new[] { Row(1) }).Should().Be(1);
                    pin = first.Pin;
                    pin.Should().NotBeNull();
                    owner.BeginTrans().Should().BeTrue();
                    owner.GetCollection("docs").Insert(Row(1000));
                    owned.Set();
                    exitOwner.Wait();
                });
            }) { IsBackground = true };
            var peerThread = new Thread(() =>
            {
                attempting.Set();
                peerFailure = Record.Exception(() =>
                {
                    peer.BeginTrans().Should().BeTrue();
                    peer.GetCollection("docs").Insert(Row(2000));
                    peer.Commit().Should().BeTrue();
                });
            }) { IsBackground = true };
            ownerThread.Start();
            var completed = false;
            var peerStarted = false;
            try
            {
                owned.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                peerThread.Start();
                peerStarted = true;
                attempting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                SpinWait.SpinUntil(() => (peerThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                Field<int>(second, "_mutexWaiters").Should().Be(0, "the waiter is still at the new local transaction gate");
                exitOwner.Set();
                ownerThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                ownerFailure.Should().BeNull();
                Field<ManualResetEventSlim>(pin, "_released").Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                pin.WaitReleased(); // actual native cleanup has completed
                completed = peerThread.Join(TimeSpan.FromSeconds(1));
                // The awakened peer may legitimately hold WRITE until its commit.
                // Probe after joining it, rather than race the handoff being tested.
                using (var probe = new SharedRangeLocks(file.Filename + "-shm"))
                {
                    probe.EnterWrite();
                    probe.ExitWrite();
                }
            }
            finally
            {
                exitOwner.Set();
                ownerThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                first.Dispose(); // wakes the leaked gate on the known-bad revision
                if (peerStarted)
                    peerThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                anchor?.Dispose();
            }
            peerFailure.Should().BeNull();
            second.Dispose();
            Verify(file, Enumerable.Range(1, 150).Concat(new[] { 2000 }).ToArray());
            completed.Should().BeTrue("owner exit must wake a waiter already asleep when the pin drops its transaction");
        }

        [OneFileFact]
        public void Deferred_facade_disposal_returns_the_pinned_transaction_lease_when_teardown_finishes()
        {
            using var file = new MappedTestFile();
            Seed(file, 150);
            var settings = new EngineSettings { Filename = file, SharedOneFile = true };
            using var first = new SharedEngine(settings);
            using var second = new SharedEngine(settings);
            using var owner = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new LiteDatabase(second, disposeOnClose: false);
            first.PinIdleLimit = first.PinHoldLimit = TimeSpan.FromMinutes(10);
            using var anchor = first.Query("docs", new Query());
            anchor.Read().Should().BeTrue();
            first.Update("docs", new[] { Row(1) }).Should().Be(1);
            var pin = first.Pin;
            pin.Should().NotBeNull();
            owner.BeginTrans().Should().BeTrue();
            var table = first.MappedReaderTable;
            var callbacks = 0;
            System.Collections.Generic.IEnumerable<BsonDocument> Input()
            {
                callbacks++;
                first.Dispose(); // cannot wait while this operation runs under its pin
                yield return Row(1000);
            }
            owner.GetCollection("docs").Insert(Input()).Should().Be(1);
            callbacks.Should().Be(1);
            Field<ManualResetEventSlim>(pin, "_released").Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var closeFailure = Record.Exception(pin.WaitReleased);
            Exception writeFailure, failure;
            try
            {
                using (var probe = new SharedRangeLocks(file.Filename + "-shm"))
                    writeFailure = Record.Exception(() => { probe.EnterWrite(); probe.ExitWrite(); });
                failure = Record.Exception(() =>
                {
                    peer.BeginTrans().Should().BeTrue();
                    peer.GetCollection("docs").Insert(Row(2000));
                    peer.Commit().Should().BeTrue();
                });
            }
            finally
            {
                // Cleanup only: the facade has already marked itself disposed, so
                // another Dispose cannot reclaim either leaked lease on this revision.
                Field<SharedLocalTransactionOwner.Lease>(first, "_localTransaction")?.Dispose();
                try { table.ExitWriter(publish: false); }
                finally { Field<NativeAdmission>(first, "_admission")?.Dispose(); }
            }
            using (new AssertionScope())
            {
                closeFailure.Should().BeNull("deferred close must retain access to its participant until WRITE is released");
                writeFailure.Should().BeNull("pin teardown has completed and no writer core remains");
                failure.Should().BeNull("a completely disposed facade cannot keep a participant-local transaction owner");
            }
        }

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static LiteEngine Core(SharedEngine engine) => Field<LiteEngine>(engine, "_engine");
        private static BsonDocument Row(int id) => new BsonDocument
        { ["_id"] = id, ["value"] = id, ["payload"] = new string('x', 512) };
        private static void Seed(string filename, int count = 1)
        {
            using var db = new LiteDatabase(filename);
            db.GetCollection("docs").Insert(Enumerable.Range(1, count).Select(Row));
            db.GetCollection("docs").EnsureIndex("value");
            db.GetCollection("unrelated").Insert(Row(42));
        }
        private static void Verify(string filename, int[] ids)
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(filename);
                db.GetCollection("docs").FindAll().Should().BeEquivalentTo(ids.Select(Row));
                foreach (var id in ids)
                    db.GetCollection("docs").Find(Query.EQ("value", id)).Should().BeEquivalentTo(new[] { Row(id) });
                new[] { db.GetCollection("unrelated").FindById(42) }.Should().BeEquivalentTo(new[] { Row(42) });
            }
        }
    }
}
#endif
