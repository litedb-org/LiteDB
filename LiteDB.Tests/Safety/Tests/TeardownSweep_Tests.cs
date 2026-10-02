using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LiteDB.Tests.Safety;
using LiteDB.Utils;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Safety.Tests
{
    /// <summary>
    /// The teardown step-fault sweep (docs/teardown-sweep.md): every registered <c>[TeardownPath]</c> is
    /// driven by its drivers, once unarmed and once per (step, occurrence, model) with one fault armed;
    /// FaultReached, FaultDisposed (against the path's declaration), ConnectionClean, Ownership,
    /// Quiescent, Durable and page-buffer leaks are checked after each run. A new path fails here until
    /// it has a driver; a new step fails until it has a catalog entry. <c>LITEDB_TEARDOWN_SWEEP=full</c>
    /// sweeps every occurrence of every nested step as well.
    /// </summary>
    public class TeardownSweep_Tests
    {
        private readonly ITestOutputHelper _output;

        public TeardownSweep_Tests(ITestOutputHelper output) => _output = output;

        public static IEnumerable<object[]> Paths => TeardownPathRegistry.Paths.Select(path => new object[] { path.Name });

        [Fact]
        public void Every_registered_teardown_path_has_a_sweep_driver()
        {
            Assert.NotEmpty(TeardownPathRegistry.Paths);
            var missing = TeardownPathRegistry.Paths.Where(path => !TeardownDrivers.For(path.Name).Any())
                .Select(path => $"new teardown path {path.Name} ({path.Member}) has no sweep driver").ToArray();
            Assert.True(missing.Length == 0, string.Join(Environment.NewLine, missing));
            var duplicates = TeardownPathRegistry.Paths.GroupBy(path => path.Name).Where(group => group.Count() > 1).Select(group => group.Key);
            Assert.Empty(duplicates);
        }

        [Fact]
        public void Every_driver_and_step_belongs_to_a_registered_path()
        {
            var problems = TeardownDrivers.All.Where(driver => TeardownPathRegistry.Find(driver.Path) == null)
                .Select(driver => $"driver {driver.Id} names no registered teardown path")
                .Concat(TeardownStepCatalog.Steps.Keys.Where(step => TeardownPathRegistry.Find(TeardownPathRegistry.OwnerOf(step)) == null)
                    .Select(step => $"catalog step {step} belongs to no registered path"))
                .Concat(TeardownKnownFindings.All.Where(finding => TeardownPathRegistry.Find(finding.Path) == null)
                    .Select(finding => $"known finding {finding.Id} names no registered path"))
                .ToArray();
            Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Teardown_disposition_mirrors_the_fault_disposition_oracle()
        {
            var library = Enum.GetValues(typeof(TeardownDisposition)).Cast<TeardownDisposition>()
                .ToDictionary(value => value.ToString(), value => (int)value);
            var oracle = Enum.GetValues(typeof(FaultDisposition)).Cast<FaultDisposition>()
                .ToDictionary(value => value.ToString(), value => (int)value);
            Assert.Equal(oracle.OrderBy(pair => pair.Key), library.OrderBy(pair => pair.Key));
        }

        /// <summary>
        /// Every catalogued step is reached by some driver's unarmed run (so the sweep arms faults there),
        /// except steps whose action exists only on another platform, each with its reason.
        /// </summary>
        [Fact]
        public void Every_catalogued_step_is_reached_by_a_driver()
        {
            var reached = new HashSet<string>(StringComparer.Ordinal);
            foreach (var driver in TeardownDrivers.All.Where(item => item.NotApplicable == null))
                foreach (var visit in TeardownSweepRunner.DefaultBaseline(driver).Visits) reached.Add(visit.Step);
            var unreached = TeardownStepCatalog.Steps.Keys.Where(step => !reached.Contains(step)).OrderBy(step => step, StringComparer.Ordinal).ToArray();
            foreach (var step in unreached.Where(step => TeardownStepCatalog.UnreachableHere(step) != null))
                _output.WriteLine($"not on this platform: {step} ({TeardownStepCatalog.UnreachableHere(step)})");
            var missing = unreached.Where(step => TeardownStepCatalog.UnreachableHere(step) == null)
                .Select(step => $"no driver reaches teardown step {step}: add a driver (or a prior state) whose path runs it").ToArray();
            Assert.True(missing.Length == 0, string.Join(Environment.NewLine, missing));
        }

        [Theory]
        [MemberData(nameof(Paths))]
        public void Teardown_path_keeps_its_contract_under_a_fault_at_every_step(string path)
        {
            var root = Path.Combine(Path.GetTempPath(), "litedb-teardown-sweep", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var clock = Stopwatch.StartNew();
            var failures = new List<string>();
            var results = new List<TeardownRunResult>();
            try
            {
                var drivers = TeardownDrivers.For(path).ToArray();
                Assert.True(drivers.Length > 0, $"new teardown path {path} has no sweep driver");
                foreach (var driver in drivers)
                {
                    var sweep = driver.NotApplicable != null ? TeardownSweepRunner.Sweep(driver, null, root, TeardownSweepPlan.Scope)
                        : TeardownSweepRunner.Sweep(driver, driver.Defaults(), root, TeardownSweepPlan.Scope,
                            baseline: TeardownSweepRunner.DefaultBaseline(driver));
                    if (sweep.NotApplicable != null)
                    {
                        _output.WriteLine($"NOT APPLICABLE {driver.Id}: {sweep.NotApplicable}");
                        continue;
                    }
                    results.AddRange(sweep.All);
                    failures.AddRange(sweep.Problems.Select(problem => driver.Id + ": " + problem));
                    failures.AddRange(sweep.Failed.Select(result => result.ToString()));
                    var cases = sweep.Cases.Count;
                    _output.WriteLine($"{driver.Id}: {cases} case(s), {sweep.Cases.Count(item => item.KnownFinding != null)} known, " +
                        $"{sweep.Failed.Count()} failed; steps reached: {string.Join(", ", sweep.Baseline?.Visits.Select(visit => visit.Step).Distinct() ?? new string[0])}");
                }
                foreach (var finding in TeardownKnownFindings.All.Where(item => item.Path == path))
                    if (!results.Any(result => result.KnownFinding == finding.Id) && drivers.Any(driver => driver.NotApplicable == null))
                        failures.Add($"known finding {finding.Id} no longer reproduces; remove it together with its fix");
                foreach (var result in results.Where(item => item.KnownFinding != null || item.Violations.Count > 0))
                    _output.WriteLine(result.ToString());
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            _output.WriteLine($"{path}: {results.Count} run(s) in {clock.Elapsed.TotalSeconds:F1} s");
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
    }
}
