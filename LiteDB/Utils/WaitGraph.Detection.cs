#if DEBUG || TESTING
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace LiteDB.Utils
{
    internal static partial class WaitGraph
    {
        private const int MaxDepth = 32;
        private const int Attempts = 4;

        /// <summary>One edge pair of a cycle: a waiter, the hold it waits for, and the thread that executes that hold.</summary>
        private sealed class Step
        {
            internal ThreadState Waiter;
            internal WaitRecord Wait;
            internal Resource Resource;
            internal Hold Hold;
            internal ThreadState Executor;
            internal bool Claimed;
        }

        /// <summary>
        /// Search for a cycle back to <paramref name="start"/>. Holds and frames are read without a
        /// global lock, so a search records the version of every node it read and accepts a cycle
        /// only if none of them lost a hold, wait or frame meanwhile: then every edge of the cycle
        /// existed at once. An unstable search is retried, then given up (a miss, never a false finding).
        /// </summary>
        private static void Check(ThreadState start, WaitRecord record)
        {
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                var versions = new List<KeyValuePair<object, int>>();
                var path = new List<Step>();
                var visited = new HashSet<ThreadState> { start };
                var stable = true;
                var found = Search(start, start, record, path, visited, versions, 0, ref stable);
                if (!stable || !Validate(versions)) continue;
                if (found) Classify(path);
                return;
            }
        }

        private static bool Search(ThreadState start, ThreadState waiter, WaitRecord wait, List<Step> path,
            HashSet<ThreadState> visited, List<KeyValuePair<object, int>> versions, int depth, ref bool stable)
        {
            if (wait == null || depth > MaxDepth) return false;
            return SearchResource(start, waiter, wait, wait.First, path, visited, versions, depth, ref stable) ||
                   SearchResource(start, waiter, wait, wait.Second, path, visited, versions, depth, ref stable);
        }

        private static bool SearchResource(ThreadState start, ThreadState waiter, WaitRecord wait, Resource resource,
            List<Step> path, HashSet<ThreadState> visited, List<KeyValuePair<object, int>> versions, int depth, ref bool stable)
        {
            if (resource == null) return false;
            var holds = resource.Snapshot(out var version);
            versions.Add(new KeyValuePair<object, int>(resource, version));
            foreach (var hold in holds)
            {
                if (ReferenceEquals(hold.Thread, waiter))
                {
                    if (wait.ExcludeOwn) continue;
                    // The waiting thread already owns a recursive primitive: its own wait is granted at once.
                    if (hold.ThreadAffine && !wait.ViaHandoff && resource.Primitive.IsRecursive()) continue;
                }
                if (hold.ThreadAffine || Observe(hold.Thread, versions, ref stable).Executes(hold.Owner, false))
                {
                    if (Follow(start, waiter, wait, resource, hold, hold.Thread, false, path, visited, versions, depth, ref stable)) return true;
                }
                if (hold.ThreadAffine) continue;
                foreach (var claimer in Claimers(hold.Owner))
                {
                    if (!Observe(claimer, versions, ref stable).Executes(hold.Owner, true)) continue;
                    if (Follow(start, waiter, wait, resource, hold, claimer, true, path, visited, versions, depth, ref stable)) return true;
                }
            }
            return false;
        }

        private static bool Follow(ThreadState start, ThreadState waiter, WaitRecord wait, Resource resource, Hold hold,
            ThreadState executor, bool claimed, List<Step> path, HashSet<ThreadState> visited,
            List<KeyValuePair<object, int>> versions, int depth, ref bool stable)
        {
            path.Add(new Step { Waiter = waiter, Wait = wait, Resource = resource, Hold = hold, Executor = executor, Claimed = claimed });
            if (ReferenceEquals(executor, start)) return true;
            if (visited.Add(executor))
            {
                var next = Observe(executor, versions, ref stable).Wait;
                if (Search(start, executor, next, path, visited, versions, depth + 1, ref stable)) return true;
            }
            path.RemoveAt(path.Count - 1);
            return false;
        }

        /// <summary>Record a thread's version before reading its waits or frames.</summary>
        private static ThreadState Observe(ThreadState state, List<KeyValuePair<object, int>> versions, ref bool stable)
        {
            var version = Volatile.Read(ref state.Version);
            if ((version & 1) != 0) stable = false;
            versions.Add(new KeyValuePair<object, int>(state, version));
            return state;
        }

        private static bool Validate(List<KeyValuePair<object, int>> versions)
        {
            foreach (var entry in versions)
            {
                var current = entry.Key is Resource resource
                    ? Volatile.Read(ref resource.Version)
                    : Volatile.Read(ref ((ThreadState)entry.Key).Version);
                if (current != entry.Value) return false;
            }
            return true;
        }

        private static void Classify(List<Step> path)
        {
            var bounded = false;
            foreach (var step in path) bounded |= step.Wait.Bound.Kind != WaitBoundKind.Unbounded;
            WaitRule rule;
            string reason;
            if (path.Count == 1)
            {
                rule = WaitRule.SelfWait;
                reason = "the holder executes on the waiting thread (length 1)" + (bounded ? "; the wait is bounded" : "");
            }
            else if (!bounded)
            {
                rule = WaitRule.UnboundedCycle;
                reason = $"every wait is unbounded (length {path.Count})";
            }
            else
            {
                rule = WaitRule.BoundedCycle;
                reason = $"a bounded wait ends it; allowed when the outcomes are correct (length {path.Count})";
            }
            Latch(rule, Signature(path), Format(path, rule.Id() + ": " + reason), path[0].Wait.Started);
        }

        private static string Signature(List<Step> path)
        {
            var parts = new List<string>();
            foreach (var step in path) parts.Add(step.Resource.Kind + "@" + step.Wait.Site);
            parts.Sort(System.StringComparer.Ordinal);
            return string.Join(" | ", parts);
        }

        private static string Format(List<Step> path, string rule)
        {
            var text = new StringBuilder();
            text.Append("Wait-for cycle, ").Append(rule).Append(':');
            foreach (var step in path)
            {
                var wait = step.Wait;
                text.AppendLine();
                text.Append("  ").Append(step.Waiter).Append(" waits (").Append(wait.Bound).Append(", ")
                    .Append(wait.Origin.ToString().ToLowerInvariant()).Append(wait.ViaHandoff ? ", via a helper thread" : "")
                    .Append(") at ").Append(wait.Site ?? "?");
                if (wait.Owner != null) text.Append(" for ").Append(Describe(wait.Owner));
                text.Append(" on ").Append(step.Resource).Append(" [").Append(step.Resource.Primitive).Append(']');
                text.AppendLine();
                text.Append("    held by ").Append(Describe(step.Hold.Owner));
                if (step.Hold.Site != null) text.Append(" (acquired at ").Append(step.Hold.Site).Append(')');
                text.Append(step.Hold.ThreadAffine ? ", bound to " : step.Claimed ? ", claimed by a teardown frame on " : ", executing on ")
                    .Append(step.Executor);
            }
            text.AppendLine();
            text.Append("  frames (innermost first):");
            var threads = new HashSet<ThreadState>();
            foreach (var step in path)
            {
                if (!threads.Add(step.Executor)) continue;
                text.AppendLine().Append("    ").Append(step.Executor).Append(": ");
                var first = true;
                for (var frame = step.Executor.Frames; frame != null; frame = frame.Outer)
                {
                    text.Append(first ? "" : " < ").Append(Describe(frame.Owner)).Append(frame.ClaimsAll ? " (claims all)" : "");
                    first = false;
                }
                if (first) text.Append("none");
            }
            return text.ToString();
        }
    }
}
#endif
