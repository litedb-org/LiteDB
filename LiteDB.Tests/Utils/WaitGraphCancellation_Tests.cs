using System;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Utils
{
    /// <summary>
    /// How a cancellable wait classifies a cycle (docs/wait-for-graph.md, "Bounds"). A cancellation
    /// ends a cycle only once it is requested: until then the cycle waits for the application to close
    /// or cancel, which it may never do, so it is an <c>unbounded-cycle</c>. A timeout, or a
    /// cancellation already requested, makes it a <c>bounded-cycle</c>.
    /// </summary>
    public class WaitGraphCancellation_Tests : IDisposable
    {
        private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(10);
        private readonly IDisposable _enabled = WaitGraph.Force();

        public void Dispose() => _enabled.Dispose();

        public enum PeerBound
        {
            TokenNeverCancelled,
            TokenAlreadyCancelled,
            CancellationWithoutToken,
            TokenThatCannotBeCancelled,
            Timeout,
        }

        [Theory]
        [InlineData(PeerBound.TokenNeverCancelled, "unbounded-cycle", "only cancellable")]
        [InlineData(PeerBound.CancellationWithoutToken, "unbounded-cycle", "only cancellable")]
        [InlineData(PeerBound.TokenThatCannotBeCancelled, "unbounded-cycle", "every wait is unbounded")]
        [InlineData(PeerBound.TokenAlreadyCancelled, "bounded-cycle", "cancellation, requested")]
        [InlineData(PeerBound.Timeout, "bounded-cycle", "timeout 5000 ms")]
        public void A_cancellation_bounds_a_cycle_only_once_it_is_requested(PeerBound kind, string expected, string text)
        {
            using var source = new CancellationTokenSource();
            if (kind == PeerBound.TokenAlreadyCancelled) source.Cancel();
            var bound = kind switch
            {
                PeerBound.TokenNeverCancelled => WaitBound.CancelledBy(source.Token),
                PeerBound.TokenAlreadyCancelled => WaitBound.CancelledBy(source.Token),
                PeerBound.CancellationWithoutToken => WaitBound.Cancellation,
                PeerBound.TokenThatCannotBeCancelled => WaitBound.CancelledBy(CancellationToken.None),
                _ => WaitBound.After(TimeSpan.FromSeconds(5)),
            };

            var finding = this.Cycle(bound, WaitBound.Unbounded);

            finding.Rule.Id().Should().Be(expected);
            finding.Fails.Should().Be(WaitGraph.IsFailing(finding.Rule));
            finding.Text.Should().Contain("length 2").And.Contain(text);
        }

        [Fact]
        public void A_timeout_anywhere_in_the_cycle_bounds_it_even_beside_an_unrequested_cancellation()
        {
            using var source = new CancellationTokenSource();

            var finding = this.Cycle(WaitBound.CancelledBy(source.Token), WaitBound.After(TimeSpan.FromSeconds(2)));

            finding.Rule.Should().Be(WaitRule.BoundedCycle);
            finding.Text.Should().Contain("cancellation, not requested").And.Contain("timeout 2000 ms");
        }

        [Fact]
        public void A_cancellation_requested_after_the_cycle_formed_does_not_retract_the_finding()
        {
            // The cycle existed with nothing able to end it; a later close or cancel resolves it, but
            // the operations hung until then.
            using var source = new CancellationTokenSource();
            var bound = WaitBound.CancelledBy(source.Token);
            var finding = this.Cycle(bound, WaitBound.Unbounded);
            source.Cancel();

            finding.Rule.Should().Be(WaitRule.UnboundedCycle);
            bound.EndsByItself.Should().BeTrue("the bound reads its token when a cycle is classified");
        }

        /// <summary>A peer holds a and waits for b with <paramref name="peer"/>; this thread holds b and waits for a with <paramref name="main"/>.</summary>
        private WaitGraph.Finding Cycle(WaitBound peer, WaitBound main)
        {
            var first = WaitGraph.Create("test-a", WaitPrimitive.SemaphoreSlim, Guid.NewGuid().ToString("N"));
            var second = WaitGraph.Create("test-b", WaitPrimitive.SemaphoreSlim, Guid.NewGuid().ToString("N"));
            var site = "cancellable " + Guid.NewGuid().ToString("N");
            using var registered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            WaitGraph.Acquired(second, site: "main holds b");
            var other = new Thread(() =>
            {
                WaitGraph.Acquired(first, site: "peer holds a");
                using (WaitGraph.Wait(second, peer, site + " peer waits b"))
                {
                    registered.Set();
                    release.Wait(Prompt);
                }
                WaitGraph.Released(first);
            }) { IsBackground = true, Name = "wait-graph cancellable peer" };
            other.Start();
            try
            {
                registered.Wait(Prompt).Should().BeTrue();
                WaitGraph.Wait(first, main, site + " main waits a").Dispose();
            }
            finally
            {
                WaitGraph.Released(second);
                release.Set();
                other.Join(Prompt).Should().BeTrue();
            }
            return WaitGraph.TakeFindings().Where(x => x.Signature.Contains(site)).Should().ContainSingle().Which;
        }
    }
}
