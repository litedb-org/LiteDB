using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// A family of forced schedules. A scenario runs on the controller thread, drives the actors of
    /// <see cref="ExplorerRun"/>, makes every scheduling decision from (variant, seed, configuration)
    /// only, and judges outcomes against permitted-outcome rules stated in its description.
    /// Scenarios are discovered by reflection, so an adapter directory copied into a fork tree adds
    /// its own (the handle alphabet) without editing this file.
    /// </summary>
    internal interface IExplorerScenario
    {
        /// <summary>Stable kebab-case name; part of vectors and fingerprints.</summary>
        string Name { get; }

        string Description { get; }

        /// <summary>Number of variants (forced orders and release points) per configuration.</summary>
        int Variants { get; }

        /// <summary>Why this configuration does not apply to the scenario (null: it applies).</summary>
        string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access);

        void Run(ExplorerRun run, int variant);
    }

    /// <summary>
    /// A scenario whose evidence is not a forced schedule: class 2 (native-thread timing decides the
    /// interleaving). Its program text is retained with every failure, and a non-reproducing replay
    /// is classified rather than dismissed.
    /// </summary>
    internal interface IExplorerEvidence
    {
        int EvidenceClass { get; }

        /// <summary>The generated program, as stable text (trace, failure artifact).</summary>
        string Program { get; }
    }

    internal static class ExplorerScenarios
    {
        private static readonly Lazy<IExplorerScenario[]> Discovered = new Lazy<IExplorerScenario[]>(() =>
            typeof(IExplorerScenario).Assembly.GetTypes()
                .Where(type => typeof(IExplorerScenario).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface &&
                    type.GetConstructor(Type.EmptyTypes) != null)
                .Select(type => (IExplorerScenario)Activator.CreateInstance(type))
                .OrderBy(scenario => scenario.Name, StringComparer.Ordinal).ToArray());

        public static IReadOnlyList<IExplorerScenario> All => Discovered.Value;

        public static IExplorerScenario Find(string name) => All.FirstOrDefault(scenario => scenario.Name == name);

        /// <summary>
        /// The full matrix: every scenario x variant x seed bit x dimension point, for the given
        /// access kinds (default: every kind in this build). Inapplicable points are included; the
        /// runner reports them as NotApplicable, so a caller can count them.
        /// </summary>
        public static IEnumerable<ExplorerVector> Matrix(IEnumerable<string> accessKinds = null, IEnumerable<string> scenarios = null)
        {
            var kinds = (accessKinds ?? ExplorerAccessKinds.All.Select(access => access.Name)).ToArray();
            var names = scenarios?.ToArray();
            foreach (var scenario in All.Where(item => names == null || names.Contains(item.Name)))
                foreach (var configuration in Configurations(kinds))
                    for (var variant = 0; variant < scenario.Variants; variant++)
                        for (var seed = 0; seed < 2; seed++)
                            yield return new ExplorerVector
                            {
                                Scenario = scenario.Name, Variant = variant, Seed = seed, Configuration = configuration
                            };
        }

        public static IEnumerable<ExplorerConfiguration> Configurations(IEnumerable<string> accessKinds)
        {
            foreach (var mode in new[] { ExplorerMode.Direct, ExplorerMode.Shared })
                foreach (var access in accessKinds)
                    foreach (ExplorerMaintenance maintenance in Enum.GetValues(typeof(ExplorerMaintenance)))
                        foreach (ExplorerCallback callback in Enum.GetValues(typeof(ExplorerCallback)))
                            foreach (ExplorerProcess process in Enum.GetValues(typeof(ExplorerProcess)))
                                foreach (var encrypted in new[] { false, true })
                                    yield return new ExplorerConfiguration
                                    {
                                        Mode = mode, Access = access, Maintenance = maintenance, Callback = callback,
                                        Process = process, Encrypted = encrypted
                                    };
        }
    }
}
