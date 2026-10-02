using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Utils
{
    /// <summary>
    /// The instrumented blocking sites feed the wait-for graph: a second connection waiting for the Shared
    /// ownership its own thread executes is latched as a self-wait (and the refused-then-hung wait is not
    /// disturbed); releasing an ownership on another thread, or waiting for an idle reader's ownership, is
    /// not a finding; crossed collection locks are a bounded cycle.
    /// </summary>
    public class WaitGraphSites_Tests : IDisposable
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private readonly IDisposable _enabled = WaitGraph.Force();

        public void Dispose() => _enabled.Dispose();

        private static SharedMutexOwner NewOwner(string name) =>
            new SharedMutexOwner(SharedMutexFactory.Create(name), new SharedMutexTurnstile(SharedMutexFactory.Create(name + ".Turn")), () => { });

        private static string NewName() => "LiteDB-waitgraph-" + Guid.NewGuid().ToString("N");

        private static WaitGraph.Finding[] Taken(string text) =>
            WaitGraph.TakeFindings().Where(x => x.Text.Contains(text)).ToArray();

        [Fact]
        public void A_second_connection_waiting_for_the_ownership_its_thread_executes_is_a_self_wait()
        {
            var name = NewName();
            var outer = NewOwner(name);
            var peer = NewOwner(name);
            outer.Enter();
            var generation = outer.Generation;
            // Nothing is thrown: the nested wait blocks as it would without the graph. A reader disposed on
            // another thread may end the outer ownership with its generation, which lets the peer through.
            var release = new Thread(() =>
            {
                var deadline = DateTime.UtcNow + Prompt;
                while (!WaitGraph.Findings.Any(x => x.Text.Contains(name)) && DateTime.UtcNow < deadline) Thread.Sleep(5);
                outer.Exit(generation);
            }) { IsBackground = true, Name = "wait-graph releaser" };
            release.Start();

            // As inside a user callback of the outer connection's call on this thread.
            WaitGraph.Enter(outer.GraphOwner);
            try
            {
                peer.Enter();
                peer.Exit();
                peer.WaitForRelease();
            }
            finally
            {
                WaitGraph.Exit(outer.GraphOwner);
            }
            release.Join(Prompt).Should().BeTrue();

            var finding = Taken(name).Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.SelfWait);
            finding.Text.Should().Contain("SharedMutexOwner.Enter (via holder)").And.Contain("via a helper thread")
                .And.Contain("[NamedMutex]").And.Contain("length 1");
            peer.IsHeld.Should().BeFalse();
        }

        [Fact]
        public void An_ownership_ended_on_another_thread_admits_the_waiter_without_a_finding()
        {
            var name = NewName();
            var owner = NewOwner(name);
            owner.Enter();
            var generation = owner.Generation;
            Exception error = null;
            var waiter = new Thread(() =>
            {
                try
                {
                    var peer = NewOwner(name);
                    peer.Enter();
                    peer.Exit();
                    peer.WaitForRelease();
                }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "wait-graph peer waiter" };
            waiter.Start();
            Thread.Sleep(100);

            // A reader disposed on a third thread ends the ownership with its generation.
            var releaser = new Thread(() => owner.Exit(generation)) { IsBackground = true };
            releaser.Start();
            releaser.Join(Prompt).Should().BeTrue();
            waiter.Join(Prompt).Should().BeTrue();
            error.Should().BeNull();
            owner.IsHeld.Should().BeFalse();
            Taken(name).Should().BeEmpty();
        }

        [Fact]
        public void A_write_waits_for_an_idle_reader_of_another_thread_without_a_finding()
        {
            using var file = new TempFile();
            using var engine = new SharedEngine(new EngineSettings { Filename = file.Filename });
            engine.Insert("items", Enumerable.Range(1, 500).Select(i => new BsonDocument { ["_id"] = i }).ToArray(), BsonAutoId.Int32);

            // A FOR UPDATE reader streams under the connection's ownership until disposed.
            var reader = engine.Query("items", new Query { ForUpdate = true });
            reader.Read().Should().BeTrue();
            var write = new Thread(() => engine.Insert("other", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32))
            { IsBackground = true, Name = "wait-graph writer" };
            write.Start();
            write.Join(TimeSpan.FromMilliseconds(200)).Should().BeFalse("the reader keeps the ownership");

            var disposer = new Thread(() => reader.Dispose()) { IsBackground = true };
            disposer.Start();
            disposer.Join(Prompt).Should().BeTrue();
            write.Join(Prompt).Should().BeTrue();
            engine.Query("other", new Query()).ToList().Should().HaveCount(1);
            WaitGraph.Findings.Where(x => x.Rule != WaitRule.LockOrder).Should().BeEmpty();
        }

        [Fact]
        public void Crossed_collection_locks_of_two_transactions_are_a_bounded_cycle()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            engine.Pragma(Pragmas.TIMEOUT, 2);
            engine.Insert("a", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            engine.Insert("b", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32);
            using var bothLocked = new Barrier(2);
            var outcomes = new Exception[2];

            void Run(int index, string first, string second)
            {
                try
                {
                    engine.BeginTrans();
                    engine.Update(first, new[] { new BsonDocument { ["_id"] = 1, ["by"] = index } });
                    bothLocked.SignalAndWait(Prompt);
                    if (index == 1) Thread.Sleep(200);
                    engine.Update(second, new[] { new BsonDocument { ["_id"] = 1, ["by"] = index } });
                    engine.Commit();
                }
                catch (Exception ex)
                {
                    outcomes[index] = ex;
                    engine.Rollback();
                }
            }

            var threads = new[]
            {
                new Thread(() => Run(0, "a", "b")) { IsBackground = true, Name = "wait-graph tx a-b" },
                new Thread(() => Run(1, "b", "a")) { IsBackground = true, Name = "wait-graph tx b-a" },
            };
            foreach (var thread in threads) thread.Start();
            foreach (var thread in threads) thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

            // The accepted outcome: a loser times out and rolls back; the cycle is bounded and only reported.
            outcomes.Where(x => x != null).Should().NotBeEmpty().And.OnlyContain(x => x is LiteException);
            var finding = Taken("CollectionLock.TryEnter").Should().ContainSingle(x => x.Rule != WaitRule.LockOrder).Which;
            finding.Rule.Should().Be(WaitRule.BoundedCycle);
            finding.Text.Should().Contain("wait-graph tx a-b").And.Contain("wait-graph tx b-a").And.Contain("timeout 2000 ms");
        }
    }
}
