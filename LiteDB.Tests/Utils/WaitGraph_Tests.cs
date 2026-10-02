using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Utils
{
    /// <summary>
    /// The wait-for graph's classification on synthetic resources. Findings are latched, never thrown:
    /// a length-1 self-wait (also when bounded), an unbounded cycle, a bounded cycle (allowed), a lock-order
    /// inversion across threads; recursion on a recursive primitive, an idle owner another thread
    /// releases, and one thread taking both orders are not findings. Driver edges (joins) take part.
    /// </summary>
    public class WaitGraph_Tests : IDisposable
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private readonly IDisposable _enabled = WaitGraph.Force();

        public void Dispose() => _enabled.Dispose();

        private static WaitGraph.Resource NewResource(string kind, WaitPrimitive primitive = WaitPrimitive.SemaphoreSlim) =>
            WaitGraph.Create(kind, primitive, Guid.NewGuid().ToString("N"));

        private static Thread Start(string name, Action body)
        {
            var thread = new Thread(() => body()) { IsBackground = true, Name = name };
            thread.Start();
            return thread;
        }

        /// <summary>Findings of this test whose signature mentions <paramref name="site"/>; taken so the hook does not report them twice.</summary>
        private static WaitGraph.Finding[] Taken(string site) =>
            WaitGraph.TakeFindings().Where(x => x.Signature.Contains(site)).ToArray();

        [Fact]
        public void The_check_hook_runs_for_every_test()
        {
            WaitGraphCheckAttribute.Started.Should().BeGreaterThan(0);
            WaitGraph.Context.Should().Contain(nameof(The_check_hook_runs_for_every_test) + " #");
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_wait_for_an_owner_executing_on_the_waiting_thread_is_a_self_wait(bool bounded)
        {
            var resource = NewResource("test-ownership", WaitPrimitive.Ownership);
            var owner = new object();
            var site = "nested call " + Guid.NewGuid().ToString("N");
            WaitGraph.Enter(owner);
            WaitGraph.Acquired(resource, owner, site: "outer call");
            try
            {
                // Recorded before blocking; the wait itself is never disturbed.
                WaitGraph.Wait(resource, bounded ? WaitBound.After(TimeSpan.FromSeconds(2)) : WaitBound.Unbounded, site).Dispose();
            }
            finally
            {
                WaitGraph.Released(resource, owner);
                WaitGraph.Exit(owner);
            }

            var finding = Taken(site).Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.SelfWait);
            finding.Fails.Should().BeFalse("every rule only reports in this milestone");
            finding.Text.Should().Contain("length 1").And.Contain(Thread.CurrentThread.ManagedThreadId.ToString())
                .And.Contain(bounded ? "timeout 2000 ms" : "unbounded");
        }

        [Theory]
        [InlineData("Monitor", false)]
        [InlineData("NamedMutex", false)]
        [InlineData("SemaphoreSlim", true)]
        public void A_same_thread_wait_is_valid_only_on_a_recursive_primitive(string name, bool selfWait)
        {
            var primitive = (WaitPrimitive)Enum.Parse(typeof(WaitPrimitive), name);
            var resource = NewResource("test-recursion", primitive);
            var site = "re-enter " + primitive + " " + Guid.NewGuid().ToString("N");
            WaitGraph.Acquired(resource, site: "first entry");
            WaitGraph.Wait(resource, WaitBound.Unbounded, site).Dispose();
            WaitGraph.Released(resource);

            Taken(site).Select(x => x.Rule).Should().BeEquivalentTo(selfWait ? new[] { WaitRule.SelfWait } : new WaitRule[0]);
        }

        [Fact]
        public void A_handoff_to_a_helper_thread_for_a_mutex_this_thread_holds_is_a_self_wait()
        {
            // A Mutex is recursive only for the thread that waits on it; a holder thread waiting for
            // this thread's mutex never gets it.
            var mutex = NewResource("test-mutex", WaitPrimitive.NamedMutex);
            var site = "via holder " + Guid.NewGuid().ToString("N");
            WaitGraph.Acquired(mutex, site: "direct ownership");
            WaitGraph.Wait(mutex, WaitBound.Unbounded, site, viaHandoff: true).Dispose();
            WaitGraph.Released(mutex);

            Taken(site).Should().ContainSingle().Which.Rule.Should().Be(WaitRule.SelfWait);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_cycle_across_two_threads_is_unbounded_or_bounded(bool bounded)
        {
            var first = NewResource("test-a");
            var second = NewResource("test-b");
            var site = "two-thread " + Guid.NewGuid().ToString("N");
            using var registered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            WaitGraph.Acquired(second, site: "main holds b");
            var other = Start("wait-graph peer", () =>
            {
                WaitGraph.Acquired(first, site: "peer holds a");
                using (WaitGraph.Wait(second, bounded ? WaitBound.After(TimeSpan.FromSeconds(5)) : WaitBound.Unbounded, site + " peer waits b"))
                {
                    registered.Set();
                    release.Wait(Prompt);
                }
                WaitGraph.Released(first);
            });
            try
            {
                registered.Wait(Prompt).Should().BeTrue();
                WaitGraph.Wait(first, WaitBound.Unbounded, site + " main waits a").Dispose();
            }
            finally
            {
                WaitGraph.Released(second);
                release.Set();
                other.Join(Prompt).Should().BeTrue();
            }

            var finding = Taken(site).Should().ContainSingle().Which;
            finding.Rule.Should().Be(bounded ? WaitRule.BoundedCycle : WaitRule.UnboundedCycle);
            finding.Text.Should().Contain("length 2").And.Contain("wait-graph peer");
        }

        [Fact]
        public void A_driver_join_of_a_thread_that_waits_for_the_joiner_is_a_cycle()
        {
            var resource = NewResource("test-driver", WaitPrimitive.Event);
            var site = "driver " + Guid.NewGuid().ToString("N");
            using var registered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            // The test thread owes the event the worker waits for, then joins the worker.
            WaitGraph.Acquired(resource, site: "test owes the signal");
            var worker = Start("wait-graph worker", () =>
            {
                using (WaitGraph.DriverWait(resource, WaitBound.Unbounded, site + " worker waits for the test"))
                {
                    registered.Set();
                    release.Wait(Prompt);
                }
            });
            registered.Wait(Prompt).Should().BeTrue();
            using (WaitGraph.Join(worker, WaitBound.Unbounded, site + " test joins the worker"))
            {
                WaitGraph.Released(resource);
                release.Set();
                worker.Join(Prompt).Should().BeTrue();
            }

            var finding = Taken(site).Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.UnboundedCycle);
            finding.Text.Should().Contain("driver").And.Contain("ThreadJoin");
        }

        [Fact]
        public void A_lock_order_inverted_on_another_thread_is_reported()
        {
            var kindA = "test-order-a-" + Guid.NewGuid().ToString("N");
            var kindB = "test-order-b-" + Guid.NewGuid().ToString("N");
            var a = WaitGraph.Create(kindA, WaitPrimitive.Monitor, "a");
            var b = WaitGraph.Create(kindB, WaitPrimitive.Monitor, "b");
            WaitGraph.Acquired(a);
            WaitGraph.Acquired(b);
            WaitGraph.Released(b);
            WaitGraph.Released(a);
            // One thread taking both orders at different times cannot deadlock with itself.
            WaitGraph.Acquired(b);
            WaitGraph.Acquired(a);
            WaitGraph.Released(a);
            WaitGraph.Released(b);
            WaitGraph.Findings.Should().NotContain(x => x.Signature.Contains(kindA));

            Start("wait-graph inverted", () =>
            {
                WaitGraph.Acquired(b);
                WaitGraph.Acquired(a);
                WaitGraph.Released(a);
                WaitGraph.Released(b);
            }).Join(Prompt).Should().BeTrue();

            var finding = Taken(kindA).Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.LockOrder);
            finding.Text.Should().Contain("wait-graph inverted");
        }

        [Fact]
        public void An_idle_owner_is_waited_for_until_another_thread_releases_it()
        {
            var resource = NewResource("test-idle", WaitPrimitive.Ownership);
            var owner = new object();
            var site = "idle " + Guid.NewGuid().ToString("N");
            WaitGraph.Acquired(resource, owner, site: "idle reader");
            using var registered = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            var waiter = Start("wait-graph waiter", () =>
            {
                using (WaitGraph.Wait(resource, WaitBound.Unbounded, site))
                {
                    registered.Set();
                    released.Wait(Prompt);
                }
            });
            registered.Wait(Prompt).Should().BeTrue();
            // The owner is held on this thread but executes no frame, so its release needs no progress here.
            using (WaitGraph.Wait(NewResource("test-unrelated"), WaitBound.Unbounded, site + " unrelated")) { }
            Start("wait-graph releaser", () => WaitGraph.Released(resource, owner)).Join(Prompt).Should().BeTrue();
            released.Set();
            waiter.Join(Prompt).Should().BeTrue();

            Taken(site).Should().BeEmpty();
        }

        [Fact]
        public void A_teardown_frame_claims_every_hold_of_its_owner()
        {
            var resource = NewResource("test-claim", WaitPrimitive.Ownership);
            var owner = new object();
            var site = "teardown " + Guid.NewGuid().ToString("N");
            Start("wait-graph idle holder", () => WaitGraph.Acquired(resource, owner, site: "idle hold")).Join(Prompt).Should().BeTrue();

            WaitGraph.Enter(owner, claimsAll: true);
            WaitGraph.Wait(resource, WaitBound.Unbounded, site + " callback inside teardown").Dispose();
            WaitGraph.Exit(owner);
            // Once the teardown frame ended, the same hold is idle again.
            WaitGraph.Wait(resource, WaitBound.Unbounded, site + " after teardown").Dispose();
            WaitGraph.Released(resource, owner);

            var finding = Taken(site).Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.SelfWait);
            finding.Text.Should().Contain("claimed by a teardown frame").And.Contain("callback inside teardown");
        }

        [Fact]
        public void A_rule_switched_to_failing_fails_the_harness_not_the_wait()
        {
            var resource = NewResource("test-failing");
            var site = "failing " + Guid.NewGuid().ToString("N");
            WaitGraph.SetFailing(WaitRule.SelfWait, true);
            try
            {
                WaitGraph.Acquired(resource, site: "own hold");
                Action wait = () => WaitGraph.Wait(resource, WaitBound.Unbounded, site).Dispose();
                wait.Should().NotThrow("library wait sites never throw");
                WaitGraph.Released(resource);

                var findings = Taken(site);
                findings.Should().ContainSingle().Which.Fails.Should().BeTrue();
                Action verdict = () => WaitGraph.ThrowIfFailing(findings);
                verdict.Should().Throw<DeadlockDetectedException>().Which.Findings.Should().Contain(site);
            }
            finally
            {
                WaitGraph.SetFailing(WaitRule.SelfWait, false);
            }
        }
    }
}
