using System;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using LiteDB.Utils;
using Xunit;
using Xunit.Sdk;

namespace LiteDB.Tests.Utils
{
    /// <summary>
    /// Which wait-for graph rules fail by default, and that the xUnit hook fails a test for them. A rule
    /// fails by default only with a proof (docs/wait-for-graph.md, "Classification and failure rules"):
    /// self-wait and unbounded cycles. Bounded cycles are allowed when outcomes are correct; lock order
    /// is advisory.
    /// </summary>
    public class WaitGraphRules_Tests : IDisposable
    {
        private readonly IDisposable _enabled = WaitGraph.Force();

        public void Dispose() => _enabled.Dispose();

        [Theory]
        [InlineData(null, "self-wait,unbounded-cycle")]
        [InlineData("", "self-wait,unbounded-cycle")]
        [InlineData("none", "")]
        [InlineData("default", "self-wait,unbounded-cycle")]
        [InlineData("default,lock-order", "self-wait,unbounded-cycle,lock-order")]
        [InlineData("self-wait", "self-wait")]
        [InlineData("lock-order", "lock-order")]
        [InlineData("all", "self-wait,unbounded-cycle,bounded-cycle,lock-order")]
        public void The_fail_setting_selects_rules_and_defaults_to_the_proven_ones(string value, string expected)
        {
            WaitGraph.ParseFailing(value).Select(x => x.Id())
                .Should().BeEquivalentTo(expected.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
        }

        [Fact]
        public void Self_wait_and_unbounded_cycles_fail_by_default_bounded_cycles_and_lock_order_only_report()
        {
            WaitGraph.DefaultFailing.Should().BeEquivalentTo(new[] { WaitRule.SelfWait, WaitRule.UnboundedCycle });
        }

        [Fact]
        public void The_test_hook_fails_a_test_that_latched_a_finding_of_a_failing_rule()
        {
            var previous = WaitGraph.IsFailing(WaitRule.SelfWait);
            WaitGraph.SetFailing(WaitRule.SelfWait, true);
            var hook = new WaitGraphCheckAttribute();
            var method = (MethodInfo)MethodBase.GetCurrentMethod();
            var resource = WaitGraph.Create("test-hook", WaitPrimitive.SemaphoreSlim, Guid.NewGuid().ToString("N"));
            try
            {
                hook.Before(method);
                WaitGraph.Acquired(resource, site: "own hold");
                WaitGraph.Wait(resource, WaitBound.Unbounded, "hook self-wait").Dispose();
                WaitGraph.Released(resource);

                Action after = () => hook.After(method);
                after.Should().Throw<XunitException>().Which.Message.Should().Contain("self-wait").And.Contain("hook self-wait");
            }
            finally
            {
                WaitGraph.TakeFindings();
                WaitGraph.SetFailing(WaitRule.SelfWait, previous);
            }
        }

        [Fact]
        public void The_test_hook_reports_but_does_not_fail_for_a_reporting_rule()
        {
            var previous = WaitGraph.IsFailing(WaitRule.LockOrder);
            WaitGraph.SetFailing(WaitRule.LockOrder, false);
            var hook = new WaitGraphCheckAttribute();
            var method = (MethodInfo)MethodBase.GetCurrentMethod();
            try
            {
                hook.Before(method);
                var a = WaitGraph.Create("test-hook-order-a", WaitPrimitive.Monitor, Guid.NewGuid().ToString("N"));
                var b = WaitGraph.Create("test-hook-order-b", WaitPrimitive.Monitor, Guid.NewGuid().ToString("N"));
                WaitGraph.Acquired(a);
                WaitGraph.Acquired(b);
                WaitGraph.Released(b);
                WaitGraph.Released(a);
                var other = new System.Threading.Thread(() =>
                {
                    WaitGraph.Acquired(b);
                    WaitGraph.Acquired(a);
                    WaitGraph.Released(a);
                    WaitGraph.Released(b);
                });
                other.Start();
                other.Join();
                WaitGraph.Findings.Should().Contain(x => x.Rule == WaitRule.LockOrder);

                Action after = () => hook.After(method);
                after.Should().NotThrow();
            }
            finally
            {
                WaitGraph.TakeFindings();
                WaitGraph.SetFailing(WaitRule.LockOrder, previous);
            }
        }
    }
}
