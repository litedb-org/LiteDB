using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Shrinks a failing case: drops suffix threads, units, and commands inside units (via
    /// <see cref="IAccessKind.ShrinkUnit"/>) while the failure keeps its identity. Parallel
    /// candidates run up to <see cref="PropertyOptions.ShrinkAttempts"/> times because thread
    /// scheduling is not replayed; a candidate counts as failing if any run fails.
    /// </summary>
    public static class CaseShrinker
    {
        public static PropertyCase Shrink(PropertyCase failing, CaseFailure failure, PropertyOptions options, TextWriter log,
            out CaseFailure lastFailure, out int runs)
        {
            var current = failing;
            lastFailure = failure;
            runs = 0;
            var progress = true;
            while (progress && runs < options.ShrinkBudget)
            {
                progress = false;
                foreach (var candidate in Candidates(current))
                {
                    if (runs >= options.ShrinkBudget) break;
                    var reproduced = Reproduce(candidate, failure.Identity, options, options.ShrinkAttemptsFor(candidate.IsParallel), ref runs);
                    if (reproduced == null) continue;
                    current = candidate;
                    lastFailure = reproduced;
                    progress = true;
                    log?.WriteLine($"  shrunk to {current.CommandCount} commands");
                    break;
                }
            }
            return current;
        }

        /// <summary>Run a case up to <paramref name="attempts"/> times; the first failure with the identity, or null.</summary>
        public static CaseFailure Reproduce(PropertyCase propertyCase, string identity, PropertyOptions options, int attempts, ref int runs)
        {
            for (var i = 0; i < attempts; i++)
            {
                runs++;
                var failure = CaseEvaluator.Evaluate(propertyCase, options);
                if (failure != null && failure.Identity == identity) return failure;
            }
            return null;
        }

        /// <summary>How many of <paramref name="runs"/> fresh executions fail with the identity.</summary>
        public static int CountReproductions(PropertyCase propertyCase, string identity, PropertyOptions options, int runs)
        {
            var count = 0;
            var spent = 0;
            for (var i = 0; i < runs; i++)
            {
                if (Reproduce(propertyCase, identity, options, 1, ref spent) != null) count++;
            }
            return count;
        }

        private static IEnumerable<PropertyCase> Candidates(PropertyCase current)
        {
            if (current.Suffixes.Count > 1)
            {
                for (var t = 1; t < current.Threads; t++) yield return current.WithoutSuffix(t);
            }
            for (var t = 0; t < current.Threads; t++)
            {
                var units = current.Units(t);
                for (var i = 0; i < units.Count; i++)
                {
                    // A parallel case keeps at least one command per suffix thread.
                    if (t > 0 && units.Count == 1) continue;
                    yield return current.WithUnits(t, units.Where((_, index) => index != i));
                }
            }
            for (var t = 0; t < current.Threads; t++)
            {
                var units = current.Units(t);
                for (var i = 0; i < units.Count; i++)
                {
                    foreach (var smaller in AccessKinds.Get(units[i].Kind).ShrinkUnit(units[i]))
                    {
                        var index = i;
                        yield return current.WithUnits(t, units.Select((u, j) => j == index ? smaller : u));
                    }
                }
            }
        }
    }
}
