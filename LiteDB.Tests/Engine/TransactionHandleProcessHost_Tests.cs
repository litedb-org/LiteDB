using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleProcessHost_Tests
    {
        internal static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };
        internal static int Count(LiteEngine engine, string name)
        {
            using var reader = engine.Query(name, new Query());
            var count = 0;
            while (reader.Read()) count++;
            return count;
        }

        [Fact]
        public void Separate_handle_and_facade_slots_preserve_legacy_and_ordinary_commits()
        {
            using var file = new TempFile();
            using (var a = new LiteEngine(file.Filename))
            using (var b = new LiteEngine(file.Filename))
            {
                var first = a.BeginHandleTransaction();
                var second = a.BeginHandleTransaction();
                var peer = b.BeginHandleTransaction();
                Assert.NotSame(first.Lease, second.Lease);
                Assert.Same(first.Host, peer.Host);
                Assert.NotSame(first.Facade, peer.Facade);
                Assert.Throws<InvalidOperationException>(() => b.ValidateHandleContext(first));
                Assert.True(a.BeginTrans());
                a.Insert("legacy", new[] { Row(10) }, BsonAutoId.Int32);
                Assert.Equal(141, Assert.Throws<LiteException>(() => first.Invoke(e => e.Insert("first", new[] { Row(1) }, BsonAutoId.Int32))).ErrorCode);
                Assert.Equal(141, Assert.Throws<LiteException>(() => second.Invoke(e => e.Insert("second", new[] { Row(2) }, BsonAutoId.Int32))).ErrorCode);
                Assert.Equal(141, Assert.Throws<LiteException>(() => peer.Invoke(e => e.Insert("peer", new[] { Row(3) }, BsonAutoId.Int32))).ErrorCode);
                TransactionHandle_Tests.OnThread(() =>
                {
                    first.Invoke(e => e.Insert("first", new[] { Row(1) }, BsonAutoId.Int32));
                    second.Invoke(e => e.Insert("second", new[] { Row(2) }, BsonAutoId.Int32));
                    peer.Invoke(e => e.Insert("peer", new[] { Row(3) }, BsonAutoId.Int32));
                });
                Assert.True(first.Invoke(e => e.Rollback()));
                Assert.True(a.Commit());
                Assert.True(second.Invoke(e => e.Commit()));
                Assert.True(peer.Invoke(e => e.Commit()));
                Assert.Throws<InvalidOperationException>(() => first.Invoke(e => e.Insert("escape", new[] { Row(4) }, BsonAutoId.Int32)));
                Assert.True(TransactionContext.IsThreadClear);
            }
            using var reopen = new LiteEngine(file.Filename);
            Assert.Equal(0, Count(reopen, "first"));
            foreach (var name in new[] { "legacy", "second", "peer" }) Assert.Equal(1, Count(reopen, name));
            Assert.Equal(0, Count(reopen, "escape"));
        }

        [Fact]
        public void Raw_and_decorated_dispatch_consume_one_ticket_and_unbound_callbacks_commit_independently()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            {
                var handle = engine.BeginHandleTransaction();
                var ordinary = new UnboundEngine(engine);
                IEnumerable<BsonDocument> Input()
                {
                    Assert.Throws<InvalidOperationException>(() => engine.Insert("escape", new[] { Row(9) }, BsonAutoId.Int32));
                    ordinary.Insert("ordinary", new[] { Row(2) }, BsonAutoId.Int32);
                    Assert.Throws<InvalidOperationException>(() => handle.Invoke(e => e.Commit()));
                    yield return Row(1);
                }
                // A decorator forwards exactly once without unwrapping; its policy executes.
                var calls = 0;
                var decorated = new DispatchForwardingEngine(engine);
                handle.Invoke(e => { calls++; return decorated.Insert("handle", Input(), BsonAutoId.Int32); });
                Assert.Equal(1, calls);
                handle.Execute(() =>
                {
                    using (TransactionContext.Dispatch(engine))
                    {
                        engine.EnsureIndex("handle", "value", "$.value", false);
                        Assert.Throws<InvalidOperationException>(() => engine.Delete("handle", new BsonValue[] { 1 }));
                    }
                    return true;
                });
                Assert.True(handle.Invoke(e => e.Rollback()));
            }
            using var reopen = new LiteEngine(file.Filename);
            Assert.Equal(1, Count(reopen, "ordinary"));
            Assert.Equal(0, Count(reopen, "handle"));
            Assert.Equal(0, Count(reopen, "escape"));
        }

        [Fact]
        public void Composed_update_and_delete_stay_bound_and_statement_failure_cannot_fall_back()
        {
            using var engine = new LiteEngine();
            engine.Insert("rows", new[] { Row(1), Row(2), Row(3) }, BsonAutoId.Int32);
            var handle = engine.BeginHandleTransaction();
            Assert.Equal(3, handle.Invoke(e => e.UpdateMany("rows", "{value: 99}", "true")));
            Assert.Equal(1, handle.Invoke(e => e.DeleteMany("rows", "_id = 1")));
            Assert.Equal(2, handle.Invoke(e => e.DeleteMany("rows", "value = 99")));
            Assert.True(handle.Invoke(e => e.Rollback()));
            Assert.Equal(3, Count(engine, "rows"));
            var failed = engine.BeginHandleTransaction();
            Assert.Throws<LiteException>(() => failed.Invoke(e => e.Insert("rows", new[] { Row(1) }, BsonAutoId.Int32)));
            Assert.Equal(LiteTransactionState.Failed, failed.Outcome);
            Assert.Throws<InvalidOperationException>(() => failed.Invoke(e => e.Insert("escape", new[] { Row(4) }, BsonAutoId.Int32)));
            Assert.Equal(0, Count(engine, "escape"));
        }

        [Fact]
        public void Bound_cursor_blocks_completion_and_callbacks_cannot_escape_dispatch()
        {
            using var engine = new LiteEngine();
            engine.Insert("rows", new[] { Row(1), Row(2), Row(3) }, BsonAutoId.Int32);
            var handle = engine.BeginHandleTransaction();
            using (var reader = handle.Invoke(e => e.Query("rows", new Query())))
            {
                Assert.Throws<LiteException>(() => handle.Invoke(e => e.Commit()));
                Assert.True(handle.Execute(() => reader.Read()));
                Assert.True(handle.Execute(() => reader.Read()));
                handle.Execute(() => { reader.Dispose(); return true; });
            }
            Assert.True(handle.Invoke(e => e.Commit()));
            Assert.Equal(LiteTransactionState.Committed, handle.Outcome);
            Assert.Throws<InvalidOperationException>(() => handle.Invoke(e => e.Query("rows", new Query())));
            Assert.True(TransactionContext.IsThreadClear);
        }

        [Fact]
        public void Decorator_policy_runs_before_forwarding_and_refusal_leaves_handle_active()
        {
            using var engine = new LiteEngine();
            engine.Insert("rows", new[] { Row(1) }, BsonAutoId.Int32);
            var handle = engine.BeginHandleTransaction();
            var decorated = new DispatchForwardingEngine(engine);
            decorated.BeforeUpdate = () => throw new NotSupportedException("decorator policy");
            Assert.Throws<NotSupportedException>(() => handle.Invoke(e => decorated.Update("rows", new[] { Row(1) })));
            Assert.True(handle.Invoke(e => e.Commit()));
            Assert.True(TransactionContext.IsThreadClear);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Known_completion_outcome_survives_cleanup_failure(bool commit)
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            {
                engine.Insert("sentinel", new[] { Row(7) }, BsonAutoId.Int32);
                var handle = engine.BeginHandleTransaction();
                handle.Invoke(e => e.Insert("rows", new[] { Row(1) }, BsonAutoId.Int32));
                var monitor = (TransactionMonitor)typeof(LiteEngine).GetField("_monitor",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(engine);
                var failure = new InvalidOperationException("completion cleanup");
                monitor.AfterTransactionExit = () => throw failure;
                try
                {
                    Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => handle.Invoke(e => commit ? e.Commit() : e.Rollback())));
                    Assert.Equal(commit ? LiteTransactionState.Committed : LiteTransactionState.RolledBack, handle.Outcome);
                }
                finally { monitor.AfterTransactionExit = null; }
            }
            for (var open = 0; open < 2; open++)
            {
                using var engine = new LiteEngine(file.Filename);
                Assert.Equal(1, Count(engine, "sentinel"));
                Assert.Equal(commit ? 1 : 0, Count(engine, "rows"));
            }
        }

        [Fact]
        public void Sequential_handoff_survives_creator_exit_and_overlap_is_refused()
        {
            using var engine = new LiteEngine();
            TransactionContext handle = null;
            var creator = new Thread(() => handle = engine.BeginHandleTransaction());
            creator.Start(); Assert.True(creator.Join(TimeSpan.FromSeconds(10)));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            IEnumerable<BsonDocument> Input()
            {
                entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); yield return Row(1);
            }
            var writer = Task.Run(() => handle.Invoke(e => e.Insert("rows", Input(), BsonAutoId.Int32)));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                Assert.Throws<InvalidOperationException>(() => handle.Invoke(e => e.Rollback()));
            }
            finally { release.Set(); }
            Assert.Equal(1, writer.GetAwaiter().GetResult());
            Assert.True(Task.Run(() => handle.Invoke(e => e.Commit())).GetAwaiter().GetResult());
            Assert.Equal(1, Count(engine, "rows"));
        }
    }
}
