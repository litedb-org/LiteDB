using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LiteDB.Tests.Mapper;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// A generated case: a sequential prefix (model thread 0) followed by suffixes that run
    /// concurrently on threads 1..n. The seed and connection mode regenerate it exactly.
    /// </summary>
    public sealed class PropertyCase
    {
        public PropertyCase(int seed, ConnectionType mode, int collections, int keys,
            IEnumerable<CommandUnit> prefix, IEnumerable<IEnumerable<CommandUnit>> suffixes)
        {
            this.Seed = seed;
            this.Mode = mode;
            this.Collections = collections;
            this.Keys = keys;
            this.Prefix = prefix.ToList();
            this.Suffixes = suffixes.Select(s => (IReadOnlyList<CommandUnit>)s.ToList()).ToList();
        }

        public int Seed { get; }
        public ConnectionType Mode { get; }
        public int Collections { get; }
        public int Keys { get; }
        public IReadOnlyList<CommandUnit> Prefix { get; }
        public IReadOnlyList<IReadOnlyList<CommandUnit>> Suffixes { get; }

        /// <summary>Model threads: the prefix thread plus one per suffix.</summary>
        public int Threads => 1 + this.Suffixes.Count;

        public bool IsParallel => this.Suffixes.Count > 0;

        public IReadOnlyList<CommandUnit> Units(int thread) => thread == 0 ? this.Prefix : this.Suffixes[thread - 1];

        public IEnumerable<PropertyCommand> Commands(int thread) => this.Units(thread).SelectMany(u => u.Commands);

        public int CommandCount => Enumerable.Range(0, this.Threads).Sum(t => this.Commands(t).Count());

        public ModelState InitialModel() => new ModelState(this.Mode, this.Collections, this.Keys, this.Threads);

        /// <summary>Copy with the units of one thread replaced (thread 0 = prefix).</summary>
        public PropertyCase WithUnits(int thread, IEnumerable<CommandUnit> units)
        {
            var list = units.ToList();
            return thread == 0
                ? new PropertyCase(this.Seed, this.Mode, this.Collections, this.Keys, list, this.Suffixes)
                : new PropertyCase(this.Seed, this.Mode, this.Collections, this.Keys, this.Prefix,
                    this.Suffixes.Select((s, i) => i == thread - 1 ? list : s));
        }

        public PropertyCase WithoutSuffix(int thread) =>
            new PropertyCase(this.Seed, this.Mode, this.Collections, this.Keys, this.Prefix,
                this.Suffixes.Where((_, i) => i != thread - 1));

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"seed={this.Seed} mode={this.Mode} collections={this.Collections} keys=1..{this.Keys} commands={this.CommandCount}");
            sb.AppendLine("  prefix T0: " + Describe(this.Prefix));
            for (var t = 1; t < this.Threads; t++) sb.AppendLine($"  suffix T{t}: " + Describe(this.Units(t)));
            return sb.ToString();
        }

        private static string Describe(IReadOnlyList<CommandUnit> units) =>
            units.Count == 0 ? "(empty)" : string.Join("; ", units.Select(u => u.ToString()));
    }

    /// <summary>Seeded generation of cases from the registered access kinds.</summary>
    public static class PropertyCaseGenerator
    {
        public static PropertyCase Parallel(int seed, PropertyOptions options)
        {
            var kinds = Kinds(options);
            var random = new StableRandom(Scramble(seed));
            var collections = 1 + random.Next(2);
            var ids = new IdSource();
            var suffixCount = random.Next(options.MinSuffixThreads, options.MaxSuffixThreads + 1);
            var prefix = Thread(random, kinds, 0, random.Next(options.MaxPrefixLength + 1), collections, options, ids);
            var suffixes = Enumerable.Range(1, suffixCount)
                .Select(t => Thread(random, kinds, t, 1 + random.Next(options.MaxSuffixLength), collections, options, ids))
                .ToList();
            return new PropertyCase(seed, options.Mode, collections, options.Keys, prefix, suffixes);
        }

        public static PropertyCase Sequential(int seed, PropertyOptions options)
        {
            var random = new StableRandom(Scramble(seed));
            var collections = 1 + random.Next(2);
            var prefix = Thread(random, Kinds(options), 0, options.SequentialLength, collections, options, new IdSource());
            return new PropertyCase(seed, options.Mode, collections, options.Keys, prefix, Enumerable.Empty<IEnumerable<CommandUnit>>());
        }

        private static IReadOnlyList<IAccessKind> Kinds(PropertyOptions options) =>
            AccessKinds.Resolve(options.AccessKindNames, out var notApplicable) ?? throw new InvalidOperationException(notApplicable);

        private static List<CommandUnit> Thread(Random random, IReadOnlyList<IAccessKind> kinds, int thread, int budget, int collections,
            PropertyOptions options, IdSource ids)
        {
            var units = new List<CommandUnit>();
            var totalWeight = kinds.Sum(k => k.Weight);
            while (budget > 0)
            {
                var pick = random.Next(totalWeight);
                var kind = kinds.First(k => (pick -= k.Weight) < 0);
                var context = new UnitGenerationContext(random, thread, budget, collections, options.Keys, options.Mode,
                    options.IncludeKnownFindings, ids.Next);
                var unit = kind.GenerateUnit(context) ?? AccessKinds.Ordinary.GenerateUnit(context);
                units.Add(unit);
                budget -= unit.Commands.Count;
            }
            return units;
        }

        /// <summary>Murmur3 finalizer, so adjacent seeds start unrelated xorshift sequences.</summary>
        private static int Scramble(int seed)
        {
            unchecked
            {
                var h = (uint)seed;
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae35;
                h ^= h >> 16;
                return (int)h;
            }
        }

        private sealed class IdSource
        {
            private int _last;
            public int Next() => ++_last;
        }
    }
}
