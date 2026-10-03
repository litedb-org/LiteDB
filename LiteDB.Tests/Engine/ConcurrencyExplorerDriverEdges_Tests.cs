using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using LiteDB.ConcurrencyTesting;
using LiteDB.Utils;
using Xunit;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The explorer's driver edges in the wait-for graph (docs/concurrency-explorer.md). The harness's
    /// own coordination is bounded by the explorer's deadlines, so the controller awaiting an actor
    /// that it still holds at a boundary is a <c>bounded-cycle</c>, never a failing one. A callback's
    /// dependency on another operation models the application's own wait and stays unbounded.
    /// </summary>
    public class ConcurrencyExplorerDriverEdges_Tests : IDisposable
    {
        private readonly IDisposable _enabled = WaitGraph.Force();
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "explorer-edges-" + Guid.NewGuid().ToString("N"));

        public ConcurrencyExplorerDriverEdges_Tests() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            _enabled.Dispose();
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
        }

        [Fact]
        public void The_controller_awaiting_an_actor_paused_at_its_own_boundary_is_not_a_failing_cycle()
        {
            using var host = new ExplorerLocalHost();
            using var schedule = new ExplorerSchedule(Path.Combine(_directory, "explorer.history"), "driver-edges", host, "test");
            var actor = schedule.NewActor("A");
            var name = "A paused " + Guid.NewGuid().ToString("N");
            var boundary = schedule.NewBoundary(name);
            var work = actor.Invoke("Paused", () => boundary.Hit());
            boundary.Wait();

            // The controller awaits A while A still waits at the boundary the controller owes; another
            // thread releases it later. Before the edges were bounded, this latched a failing
            // unbounded-cycle made only of harness waits.
            var releaser = new Thread(() =>
            {
                Thread.Sleep(300);
                boundary.Release();
            }) { IsBackground = true, Name = "driver-edges releaser" };
            releaser.Start();
            actor.Complete(work);
            releaser.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            schedule.Stop().Should().BeTrue();

            work.Ok.Should().BeTrue();
            var findings = WaitGraph.TakeFindings().Where(x => x.Signature.Contains(name)).ToArray();
            findings.Should().NotContain(x => x.Fails, "harness coordination is bounded by the explorer's deadlines");
            var finding = findings.Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.BoundedCycle);
            finding.Text.Should().Contain("explorer controller awaits").And.Contain("timeout " + ExplorerSchedule.ControllerBound.TotalMilliseconds + " ms");
        }

        [Fact]
        public void A_callback_dependency_stays_unbounded_so_a_cycle_through_it_still_fails()
        {
            // A library hold that the awaited operation needs, held by the awaiting callback's thread:
            // the dependency is the application's wait, so only the library could end this cycle.
            var resource = WaitGraph.Create("test-library-lock", WaitPrimitive.SemaphoreSlim, Guid.NewGuid().ToString("N"));
            var awaited = "1:B/" + Guid.NewGuid().ToString("N");
            using var registered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            WaitGraph.Acquired(resource, site: "callback holds the lock");
            var other = new Thread(() =>
            {
                ExplorerDriverEdges.Running(awaited);
                using (WaitGraph.Wait(resource, WaitBound.Unbounded, "awaited operation waits for the lock"))
                {
                    registered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                ExplorerDriverEdges.Finished(awaited);
            }) { IsBackground = true, Name = "driver-edges awaited" };
            other.Start();
            try
            {
                registered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                ExplorerDriverEdges.Dependency("N0", awaited, ExplorerSchedule.ControllerBound).Dispose();
            }
            finally
            {
                WaitGraph.Released(resource);
                release.Set();
                other.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
            }

            var finding = WaitGraph.TakeFindings().Where(x => x.Signature.Contains(awaited)).Should().ContainSingle().Which;
            finding.Rule.Should().Be(WaitRule.UnboundedCycle);
            finding.Text.Should().Contain("explorer dependency: N0 awaits " + awaited);
        }
    }
}
