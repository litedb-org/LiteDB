using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Vector selections. <see cref="Representative"/> is the default xUnit set: every scenario, both
    /// modes, both upstream access kinds, and for each (mode, access) half of the maintenance x
    /// callback pairs, alternating so every pair occurs in each mode; variants rotate through every
    /// callback point and order; plus the external-writer process in Shared mode.
    /// <see cref="Rotating"/> visits the full matrix from a seed-chosen start, dimensions first (fuzz).
    /// </summary>
    internal static class ExplorerSelection
    {
        public static IEnumerable<ExplorerVector> Representative()
        {
            var modes = new[] { ExplorerMode.Direct, ExplorerMode.Shared };
            var maintenances = (ExplorerMaintenance[])Enum.GetValues(typeof(ExplorerMaintenance));
            var callbacks = (ExplorerCallback[])Enum.GetValues(typeof(ExplorerCallback));
            foreach (var scenario in ExplorerScenarios.All)
            {
                var index = 0;
                for (var m = 0; m < modes.Length; m++)
                    for (var a = 0; a < ExplorerAccessKinds.Upstream.Length; a++)
                        for (var i = 0; i < maintenances.Length; i++)
                            for (var j = 0; j < callbacks.Length; j++)
                            {
                                if ((i + j + a) % 2 != 0) continue;
                                var configuration = new ExplorerConfiguration
                                {
                                    Mode = modes[m], Access = ExplorerAccessKinds.Upstream[a], Maintenance = maintenances[i],
                                    Callback = callbacks[j], Encrypted = (i + j + m) % 3 == 0
                                };
                                if (scenario.NotApplicable(configuration, ExplorerAccessKinds.Find(configuration.Access)) != null) continue;
                                yield return new ExplorerVector
                                {
                                    Scenario = scenario.Name, Variant = (index * 7) % scenario.Variants, Seed = index % 2,
                                    Configuration = configuration
                                };
                                index++;
                            }
                foreach (var maintenance in new[] { ExplorerMaintenance.None, ExplorerMaintenance.Close })
                    yield return new ExplorerVector
                    {
                        Scenario = scenario.Name, Variant = index++ % scenario.Variants, Seed = 0,
                        Configuration = new ExplorerConfiguration
                        {
                            Mode = ExplorerMode.Shared, Access = "legacy", Maintenance = maintenance, Process = ExplorerProcess.ExternalWriter
                        }
                    };
            }
        }

        /// <summary>Total number of vectors of the full matrix for <paramref name="accessKinds"/>.</summary>
        public static int Count(IEnumerable<string> accessKinds) => ExplorerScenarios.Matrix(accessKinds).Count();

        /// <summary>
        /// The <paramref name="step"/>-th vector of a complete visit of the matrix, starting at a
        /// seed-chosen offset. Each step picks a dimension point (callback, maintenance, mode, access
        /// kind, scenario, process, encryption) through a permutation of all points, so a few dozen
        /// consecutive steps spread over every dimension; consecutive steps start at different
        /// (variant, seed bit) points and each full cycle over the dimension points advances every cell
        /// by one, so the whole matrix is visited once per (dimension points x largest variant count)
        /// steps. Decided by (seed, step) only.
        /// </summary>
        public static ExplorerVector Rotating(int seed, int step, IReadOnlyList<string> accessKinds)
        {
            var callbacks = (ExplorerCallback[])Enum.GetValues(typeof(ExplorerCallback));
            var maintenances = (ExplorerMaintenance[])Enum.GetValues(typeof(ExplorerMaintenance));
            var modes = new[] { ExplorerMode.Direct, ExplorerMode.Shared };
            var processes = (ExplorerProcess[])Enum.GetValues(typeof(ExplorerProcess));
            var scenarios = ExplorerScenarios.All;
            var cells = (long)callbacks.Length * maintenances.Length * modes.Length * accessKinds.Count * scenarios.Count * processes.Length * 2;
            var k = (long)(uint)seed % cells + step;
            var cycle = k / cells;
            // A multiplicative permutation of the cells (7919 is prime and coprime with every cell count
            // below it), so a short campaign spreads over every digit; the decode is mixed radix.
            var cell = k % cells * 7919 % cells;
            var digit = cell;
            long Next(int radix)
            {
                var value = digit % radix;
                digit /= radix;
                return value;
            }
            var configuration = new ExplorerConfiguration
            {
                Callback = callbacks[Next(callbacks.Length)],
                Maintenance = maintenances[Next(maintenances.Length)],
                Mode = modes[Next(modes.Length)],
                Access = accessKinds[(int)Next(accessKinds.Count)]
            };
            var scenario = scenarios[(int)Next(scenarios.Count)];
            configuration.Process = processes[Next(processes.Length)];
            configuration.Encrypted = Next(2) == 1;
            // Consecutive cells start at different (variant, seed bit) points; per cell, successive cycles step through all of them.
            var point = (int)((cell * 5 + cycle + (uint)seed / cells) % (scenario.Variants * 2L));
            return new ExplorerVector
            {
                Scenario = scenario.Name, Variant = point / 2, Seed = point % 2, Configuration = configuration
            };
        }
    }
}
