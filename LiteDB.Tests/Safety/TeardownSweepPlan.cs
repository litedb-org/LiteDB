using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Utils;

namespace LiteDB.Tests.Safety
{
    /// <summary>How many cases a sweep derives from a baseline.</summary>
    internal enum TeardownSweepScope
    {
        /// <summary>
        /// Every step site of the path itself (first and last occurrence) under each model it allows,
        /// plus one composition case per nested path reached during it: that path's first step reached,
        /// first occurrence, under fail-inside (or skip when the step allows only that). A path without
        /// step markers of its own (it delegates its whole teardown) gets every nested step's first
        /// occurrence under each model instead.
        /// Each nested path's other steps are swept by that path's own drivers.
        /// </summary>
        Default,
        /// <summary>Every occurrence of every step site reached in the path's window (<c>LITEDB_TEARDOWN_SWEEP=full</c>).</summary>
        Full
    }

    /// <summary>Derives a driver's cases from the step sites its baseline reached.</summary>
    internal static class TeardownSweepPlan
    {
        public static TeardownSweepScope Scope =>
            string.Equals(Environment.GetEnvironmentVariable("LITEDB_TEARDOWN_SWEEP"), "full", StringComparison.OrdinalIgnoreCase)
                ? TeardownSweepScope.Full : TeardownSweepScope.Default;

        public static IReadOnlyList<TeardownCaseSpec> Cases(TeardownDriver driver, IEnumerable<TeardownVisit> baseline, TeardownSweepScope scope)
        {
            if (scope == TeardownSweepScope.Default && driver.BaselineOnlyByDefault) return new TeardownCaseSpec[0];
            var visits = baseline.ToArray();
            var last = visits.GroupBy(visit => (visit.Step, visit.Site)).ToDictionary(group => group.Key, group => group.Max(visit => visit.Occurrence));
            var cases = new List<TeardownCaseSpec>();
            var hasOwnSteps = visits.Any(visit => string.Equals(TeardownPathRegistry.OwnerOf(visit.Step), driver.Path, StringComparison.Ordinal));
            var seen = new HashSet<(string, TeardownStepSite, int)>();
            var composed = new HashSet<string>(StringComparer.Ordinal);
            // Fail-inside first, so a nested path's representative is its fail-inside case where one exists.
            var ordered = visits.Where(visit => visit.Site == TeardownStepSite.After).Concat(visits.Where(visit => visit.Site == TeardownStepSite.Before));
            foreach (var visit in scope == TeardownSweepScope.Default ? ordered : visits)
            {
                if (!seen.Add((visit.Step, visit.Site, visit.Occurrence))) continue;
                var info = TeardownStepCatalog.Find(visit.Step);
                if (info == null) continue; // the fixture reports uncatalogued steps
                var model = visit.Site == TeardownStepSite.Before ? FaultModel.Skip : FaultModel.FailInside;
                var allowed = model == FaultModel.Skip ? TeardownModels.Skip : TeardownModels.FailInside;
                if ((info.Models & allowed) == 0) continue;
                var owner = TeardownPathRegistry.OwnerOf(visit.Step);
                var own = string.Equals(owner, driver.Path, StringComparison.Ordinal);
                if (scope == TeardownSweepScope.Default)
                {
                    var keep = own ? visit.Occurrence == 1 || visit.Occurrence == last[(visit.Step, visit.Site)]
                        : !hasOwnSteps ? visit.Occurrence == 1
                        : visit.Occurrence == 1 && composed.Add(owner);
                    if (!keep) continue;
                }
                cases.Add(new TeardownCaseSpec
                {
                    Driver = driver, Step = visit.Step, Model = model, Occurrence = visit.Occurrence, JudgeDeclaration = own || !hasOwnSteps
                });
            }
            return cases;
        }
    }
}
