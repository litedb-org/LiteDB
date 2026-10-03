using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// The explorer's DRIVER edges for the wait-for graph (docs/wait-for-graph.md; FOLLOWUP item 7:
    /// callbacks and cross-thread joins are edges too), one call site per kind of driver wait:
    /// <list type="bullet">
    /// <item><see cref="Boundary"/>: an actor's callback blocks until the controller releases a forced
    /// boundary; the controller owes it from <see cref="Owe"/> until <see cref="Paid"/>.</item>
    /// <item><see cref="Dependency"/>: an operation's callback or input sequence awaits another
    /// actor's operation; the awaited actor owes it from <see cref="Running"/> until <see cref="Finished"/>.</item>
    /// <item><see cref="Controller"/>: the controller waits for an actor's operation.</item>
    /// <item><see cref="Join"/>: the controller joins an actor thread.</item>
    /// </list>
    /// The graph is reached by reflection, so the explorer still compiles and runs (without
    /// driver edges) on trees that do not have it, such as historical proof revisions.
    /// <para>Bounds: the harness's own coordination (<see cref="Boundary"/>, <see cref="Controller"/>,
    /// <see cref="Join"/>) is bounded by the explorer's deadlines (<see cref="ExplorerSchedule.ControllerBound"/>,
    /// which ends them with <c>EXPLORER_UNRELEASED_BOUNDARY</c> / <c>EXPLORER_CONTROLLER_TIMEOUT</c>), and
    /// is registered with that timeout. A cycle through one of them is a schedule the controller
    /// chose (it awaits an actor it still holds at a boundary): a <c>bounded-cycle</c>, never a
    /// failing one; only the deadline decides. A <see cref="Dependency"/> models the application's
    /// own wait (a callback awaiting another operation, which an application would do without a
    /// bound) and stays unbounded, so a library cycle through it still fails. The graph only
    /// reports; it never throws.</para>
    /// </summary>
    internal static class ExplorerDriverEdges
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly Type Graph = typeof(LiteDB.Engine.LiteEngine).Assembly.GetType("LiteDB.Utils.WaitGraph");
        private static readonly MethodInfo CreateMethod = Graph?.GetMethod("Create", Static);
        private static readonly MethodInfo AcquiredMethod = Graph?.GetMethod("Acquired", Static);
        private static readonly MethodInfo ReleasedMethod = Graph?.GetMethod("Released", Static);
        private static readonly MethodInfo DriverWaitMethod = Graph?.GetMethod("DriverWait", Static);
        private static readonly MethodInfo JoinMethod = Graph?.GetMethod("Join", Static);
        private static readonly MethodInfo RecheckMethod = Graph?.GetMethod("Recheck", Static);
        private static readonly Type BoundType = typeof(LiteDB.Engine.LiteEngine).Assembly.GetType("LiteDB.Utils.WaitBound");
        private static readonly object Unbounded = Graph == null ? null : Activator.CreateInstance(BoundType);
        private static readonly MethodInfo AfterMethod = BoundType?.GetMethod("After", Static);
        private static readonly object EventPrimitive = Graph == null ? null
            : Enum.Parse(typeof(LiteDB.Engine.LiteEngine).Assembly.GetType("LiteDB.Utils.WaitPrimitive"), "Event");
        private static readonly ConcurrentDictionary<string, Debt> Debts = new ConcurrentDictionary<string, Debt>(StringComparer.Ordinal);

        /// <summary>True when this tree has the wait-for graph.</summary>
        internal static bool Available => Graph != null && CreateMethod != null && DriverWaitMethod != null;

        internal static void Owe(string boundary) => Start("explorer-boundary", boundary);

        internal static void Paid(string boundary) => End("explorer-boundary", boundary);

        /// <summary>An actor waits at a forced boundary until the controller releases it, at most <paramref name="bound"/>.</summary>
        internal static IDisposable Boundary(string boundary, TimeSpan bound) =>
            Wait("explorer-boundary", boundary, "explorer boundary '" + boundary + "' (released by the controller)", After(bound));

        /// <summary>The calling actor owes <paramref name="operation"/>'s completion until it finishes.</summary>
        internal static void Running(string operation) => Start("explorer-operation", operation);

        internal static void Finished(string operation) => End("explorer-operation", operation);

        /// <summary>
        /// A callback awaits another actor's operation. Unbounded although the harness gives up after
        /// <paramref name="bound"/>: it stands for the application's wait, which has no bound.
        /// </summary>
        internal static IDisposable Dependency(string waiter, string awaited, TimeSpan bound) =>
            Wait("explorer-operation", awaited, "explorer dependency: " + waiter + " awaits " + awaited, Unbounded);

        /// <summary>The controller awaits an actor's operation, at most <paramref name="bound"/>.</summary>
        internal static IDisposable Controller(string awaited, TimeSpan bound) =>
            Wait("explorer-operation", awaited, "explorer controller awaits " + awaited, After(bound));

        /// <summary>The controller joins an actor thread, at most <paramref name="bound"/>.</summary>
        internal static IDisposable Join(Thread thread, TimeSpan bound)
        {
            if (!Available || JoinMethod == null) return Scope.None;
            return Invoke(JoinMethod, thread, After(bound), "explorer controller joins " + thread.Name) as IDisposable ?? Scope.None;
        }

        /// <summary>Search again for the calling thread's registered wait (each iteration of a poll loop).</summary>
        internal static void Recheck()
        {
            if (Available && RecheckMethod != null) Invoke(RecheckMethod);
        }

        private static void Start(string kind, string name)
        {
            if (!Available) return;
            var debt = Debts.AddOrUpdate(kind + ":" + name, _ => new Debt(kind, name), (_, old) => new Debt(kind, name));
            Invoke(AcquiredMethod, debt.Resource, null, false, "explorer " + kind + " '" + name + "'");
        }

        private static void End(string kind, string name)
        {
            if (!Available || !Debts.TryRemove(kind + ":" + name, out var debt)) return;
            Invoke(ReleasedMethod, debt.Resource, debt.Owner, true);
        }

        private static IDisposable Wait(string kind, string name, string site, object bound)
        {
            if (!Available || !Debts.TryGetValue(kind + ":" + name, out var debt)) return Scope.None;
            return Invoke(DriverWaitMethod, debt.Resource, bound, site) as IDisposable ?? Scope.None;
        }

        /// <summary>The graph's timeout bound; unbounded on a tree whose graph cannot express one.</summary>
        private static object After(TimeSpan bound) =>
            AfterMethod == null ? Unbounded : Invoke(AfterMethod, bound < TimeSpan.Zero ? TimeSpan.Zero : bound) ?? Unbounded;

        /// <summary>The wait-for graph's latched findings so far (not taken), as text; empty without the graph.</summary>
        internal static string[] Findings()
        {
            var property = Graph?.GetProperty("Findings", Static);
            if (!(property?.GetValue(null) is System.Collections.IEnumerable findings)) return new string[0];
            return findings.Cast<object>().Select(finding => finding.ToString()).ToArray();
        }

        private static object Invoke(MethodInfo method, params object[] arguments)
        {
            // Reporting must never change what the scenario does.
            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException) { return null; }
            catch (ArgumentException) { return null; }
        }

        private sealed class Debt
        {
            internal Debt(string kind, string name)
            {
                this.Resource = CreateMethod.Invoke(null, new[] { kind, EventPrimitive, name, (object)false });
                this.Owner = Thread.CurrentThread;
            }

            internal object Resource { get; }
            internal Thread Owner { get; }
        }

        private sealed class Scope : IDisposable
        {
            internal static readonly Scope None = new Scope();

            public void Dispose()
            {
            }
        }
    }
}
