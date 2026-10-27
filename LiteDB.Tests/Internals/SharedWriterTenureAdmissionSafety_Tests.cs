#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureAdmissionSafety_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Nested_calls_join_the_current_transaction_while_a_same_or_sibling_facade_waits_at_native_main(bool sibling)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var peer = new SharedEngine(files.Settings))
            using (var input = new ManualResetEventSlim())
            using (var waiting = new ManualResetEventSlim())
            {
                var target = sibling ? peer : engine;
                var turnstile = Field<SharedMutexTurnstile>(target, "_turnstile");
                turnstile.BeforeMainWait = () => waiting.Set();
                Exception failure = null;
                var waiter = new Thread(() => failure = Record.Exception(() =>
                {
                    input.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    target.BeginTrans().Should().BeTrue();
                    target.Update("docs", new[] { Row(2, 2) }).Should().Be(1);
                    target.Commit().Should().BeTrue();
                })) { IsBackground = true };
                waiter.Start();
                IEnumerable<BsonDocument> Input()
                {
                    input.Set();
                    waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the reserved waiter reached its actual native main wait");
                    engine.BeginTrans().Should().BeFalse();
                    engine.Update("docs", new[] { Row(1, 1) }).Should().Be(1);
                    using var nested = engine.Query("sentinel", new Query());
                    nested.Read().Should().BeTrue();
                    nested.Current.AsDocument.As<object>().Should().BeEquivalentTo(Row(42, 9));
                    ((Action)(() => peer.BeginTrans())).Should().Throw<InvalidOperationException>(
                        "a sibling callback cannot wait for the current callback's own native ownership");
                    yield return Row(1000, 1);
                }
                try { engine.Insert("admitted", Input(), BsonAutoId.Int32).Should().Be(1); }
                finally { input.Set(); waiter.Join(TimeSpan.FromSeconds(10)).Should().BeTrue(); turnstile.BeforeMainWait = null; }
                failure.Should().BeNull();
            }
            using var cold = new LiteDatabase(files.Filename);
            cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, id == 1 ? 1 : id == 2 ? 2 : 0)));
            cold.GetCollection("docs").Find(Query.EQ("value", 1)).Should().BeEquivalentTo(new[] { Row(1, 1) });
            cold.GetCollection("docs").Find(Query.EQ("value", 2)).Should().BeEquivalentTo(new[] { Row(2, 2) });
            cold.GetCollection("admitted").FindAll().Should().BeEquivalentTo(new[] { Row(1000, 1) });
            cold.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
        }

        [OneFileFact]
        public void Multiple_actual_threads_compete_at_idle_transfer_without_losing_writes_or_stale_reader_cleanup()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var start = new ManualResetEventSlim())
            {
                engine.Update("docs", new[] { Row(1, 0) });
                using var old = engine.Query("docs", new Query());
                old.Read().Should().BeTrue();
                var failures = new Exception[4];
                var workers = Enumerable.Range(0, 4).Select(worker => new Thread(() =>
                {
                    failures[worker] = Record.Exception(() =>
                    {
                        start.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                        for (var value = 1; value <= 6; value++)
                        {
                            engine.Update("docs", new[] { Row(10 + worker, value) }).Should().Be(1);
                            if (worker == 0 && value == 3) old.Dispose(); // idempotent stale snapshot cleanup races migration
                        }
                    });
                }) { IsBackground = true }).ToArray();
                foreach (var worker in workers) worker.Start();
                start.Set();
                foreach (var worker in workers) worker.Join(TimeSpan.FromSeconds(15)).Should().BeTrue();
                failures.Should().OnlyContain(error => error == null);
            }
            using var cold = new LiteDatabase(files.Filename);
            var expected = Enumerable.Range(1, 160).Select(id => Row(id, id >= 10 && id <= 13 ? 6 : 0)).ToArray();
            cold.GetCollection("docs").FindAll().Should().BeEquivalentTo(expected);
            cold.GetCollection("docs").Find(Query.EQ("value", 6)).Should().BeEquivalentTo(expected.Where(d => d["value"] == 6));
            cold.GetCollection("sentinel").Find(Query.EQ("value", 9)).Should().BeEquivalentTo(new[] { Row(42, 9) });
        }

        [OneFileTheory]
        [InlineData("write")] [InlineData("begin")] [InlineData("query")] [InlineData("pragma")]
        public void Every_new_pinned_call_after_deferred_disposal_is_refused(string operation)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            {
                engine.Update("docs", new[] { Row(1, 0) });
                var pin = engine.Pin;
                IEnumerable<BsonDocument> Input()
                {
                    engine.Dispose();
                    Action call = () =>
                    {
                        switch (operation)
                        {
                            case "write": engine.Update("docs", new[] { Row(1, 99) }); break;
                            case "begin": engine.BeginTrans(); break;
                            case "query": using (var read = engine.Query("docs", new Query())) read.Read(); break;
                            case "pragma": engine.Pragma(Pragmas.CHECKPOINT, 0); break;
                        }
                    };
                    call.Should().Throw<ObjectDisposedException>();
                    yield return Row(1000, 1);
                }
                engine.Insert("admitted", Input(), BsonAutoId.Int32).Should().Be(1);
                pin.WaitReleased();
            }
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
            cold.GetCollection("admitted").FindAll().Should().BeEquivalentTo(new[] { Row(1000, 1) });
        }
    }
}
#endif
