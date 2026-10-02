using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// A way of reaching the database (auto-commit calls, per-thread BeginTrans/Commit, ...) that the
    /// property test generates commands for, runs against the real database and models.
    /// <para>Contract for implementers. A new kind (for example "handle", an explicit transaction
    /// object API) is one class implementing this interface plus one line in
    /// <see cref="AccessKinds.All"/>:</para>
    /// <list type="number">
    /// <item><see cref="Name"/>: stable and unique; it is <see cref="PropertyCommand.Kind"/> of every
    /// command the kind creates and appears in printed counterexamples.</item>
    /// <item><see cref="Capability"/>: the library surface the kind needs (for example
    /// <c>transaction-handles</c>). A kind is registered only in builds whose library has that
    /// surface: a handle kind compiles only against revisions with the handle API (a fork worktree),
    /// never against upstream dev. A run that requests a kind that is not registered in the build
    /// reports <see cref="PropertyVerdict.NotApplicable"/>, a distinct result that is neither skipped
    /// silently nor counted as passing.</item>
    /// <item><see cref="GenerateUnit"/>: one self-contained unit for a thread, at most
    /// <see cref="UnitGenerationContext.Budget"/> commands, or null when nothing fits. Every
    /// transaction or resource the unit opens must be completed by the unit itself, so that units
    /// compose in any order and the shrinker may drop any of them. Use only
    /// <see cref="UnitGenerationContext.Random"/> (seeded replay) and
    /// <see cref="UnitGenerationContext.NextId"/> for case-unique numbers.</item>
    /// <item><see cref="ShrinkUnit"/>: strictly smaller valid variants of a unit (may be none).</item>
    /// <item><see cref="Execute"/>: run one command synchronously on the calling thread against
    /// <see cref="ThreadContext"/> and return its observation. Engine exceptions become
    /// observations through <see cref="Observation.FromException"/>; do not let them escape.</item>
    /// <item><see cref="Apply"/>: the model. Given the state just before the command takes effect
    /// on model thread <c>thread</c>, add to <c>outcomes</c> every allowed (observation, next state)
    /// pair. Add nothing when the command cannot take effect in this state (it would wait). A lock
    /// timeout is an allowed outcome only where a conflicting holder exists in this state. Never
    /// mutate <c>state</c>: clone it per outcome. Use the primitives on <see cref="ModelState"/>
    /// (transactions keyed by owner, locks, snapshots, registers) and
    /// <see cref="DataOperations.Apply"/> for collection reads and writes, so that interactions
    /// with the other kinds (lock conflicts, visibility) follow from the shared state. Transactions
    /// that do not belong to a thread use owner keys outside 0..Threads-1 (for example 1000 + handle
    /// number) and keep extra per-owner facts in <see cref="ModelState.Register"/>. An effect that
    /// spans two points returns <see cref="ModelOutcome.Completes"/> false first.</item>
    /// </list>
    /// Objects that live across commands (a handle) go in <see cref="ThreadContext.Items"/>, or in
    /// <see cref="ThreadContext.SharedItems"/> when another thread may use them.
    /// </summary>
    public interface IAccessKind
    {
        string Name { get; }

        /// <summary>The library surface this kind requires; reported with every run and NOT APPLICABLE verdict.</summary>
        string Capability { get; }

        /// <summary>Relative generation weight against the other registered kinds.</summary>
        int Weight { get; }

        CommandUnit GenerateUnit(UnitGenerationContext context);

        IEnumerable<CommandUnit> ShrinkUnit(CommandUnit unit);

        Observation Execute(PropertyCommand command, ThreadContext context);

        void Apply(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes);
    }

    /// <summary>
    /// One allowed result of a command and the state after it. A command whose effect spans two
    /// points (a write that starts waiting where a conflicting holder exists and fails later) returns
    /// an outcome with <see cref="Completes"/> false and records its phase in the state
    /// (<see cref="ModelState.Pending"/>); the checker applies the same command again at a later
    /// position, still inside the call's interval, and the kind then returns the completing outcome.
    /// </summary>
    public sealed class ModelOutcome
    {
        public ModelOutcome(Observation observation, ModelState next, bool completes = true)
        {
            this.Observation = observation;
            this.Next = next;
            this.Completes = completes;
        }

        public Observation Observation { get; }
        public ModelState Next { get; }
        public bool Completes { get; }
    }

    /// <summary>Inputs for generating one unit for one thread.</summary>
    public sealed class UnitGenerationContext
    {
        private readonly Func<int> _nextId;

        public UnitGenerationContext(Random random, int thread, int budget, int collections, int keys, ConnectionType mode,
            bool includeKnownFindings, Func<int> nextId)
        {
            this.IncludeKnownFindings = includeKnownFindings;
            this.Random = random;
            this.Thread = thread;
            this.Budget = budget;
            this.Collections = collections;
            this.Keys = keys;
            this.Mode = mode;
            _nextId = nextId;
        }

        public Random Random { get; }
        /// <summary>Model thread index (0 = the sequential prefix).</summary>
        public int Thread { get; }
        /// <summary>Maximum number of commands the unit may contain.</summary>
        public int Budget { get; }
        public int Collections { get; }
        public int Keys { get; }
        public ConnectionType Mode { get; }

        /// <summary>Whether to generate what <see cref="KnownFindings"/> rules exclude.</summary>
        public bool IncludeKnownFindings { get; }

        /// <summary>A number unique within the generated case, starting at 1.</summary>
        public int NextId() => _nextId();
    }

    /// <summary>What a command executes against: the database of the case, seen from one thread.</summary>
    public sealed class ThreadContext
    {
        private readonly ILiteCollection<BsonDocument>[] _collections;

        public ThreadContext(int thread, ILiteDatabase database, ConcurrentDictionary<int, object> sharedItems)
        {
            this.Thread = thread;
            this.Database = database;
            this.SharedItems = sharedItems;
            _collections = Enumerable.Range(0, PropertyDatabase.MaxCollections)
                .Select(c => database.GetCollection(PropertyDatabase.CollectionName(c)))
                .ToArray();
        }

        public int Thread { get; }
        public ILiteDatabase Database { get; }
        public ILiteCollection<BsonDocument> Collection(int index) => _collections[index];

        /// <summary>Objects an access kind keeps for this thread (for example open handles).</summary>
        public Dictionary<int, object> Items { get; } = new Dictionary<int, object>();

        /// <summary>Objects an access kind shares between the threads of one case.</summary>
        public ConcurrentDictionary<int, object> SharedItems { get; }
    }

    /// <summary>Registry of the access kinds the generator draws from.</summary>
    public static class AccessKinds
    {
        public static readonly OrdinaryAccess Ordinary = new OrdinaryAccess();
        public static readonly LegacyAccess Legacy = new LegacyAccess();

        /// <summary>Every kind available in this build. An extension kind adds its instance here.</summary>
        public static readonly IReadOnlyList<IAccessKind> All = new IAccessKind[]
        {
            Ordinary,
            Legacy,
        };

        public static IAccessKind Get(string name)
        {
            var kind = All.FirstOrDefault(k => k.Name == name);
            if (kind == null) throw new ArgumentException("Unknown access kind: " + name, nameof(name));
            return kind;
        }

        /// <summary>
        /// The kinds a run generates from: <paramref name="names"/> (null: every registered kind).
        /// Returns null and a reason when a requested kind is not available in this build. The
        /// ordinary kind is the baseline every revision has; it also fills budgets other kinds cannot.
        /// </summary>
        public static IReadOnlyList<IAccessKind> Resolve(IReadOnlyList<string> names, out string notApplicable)
        {
            notApplicable = null;
            if (names == null) return All;
            var missing = names.Where(n => All.All(k => k.Name != n)).ToList();
            if (missing.Count > 0)
            {
                notApplicable = $"requested access kind(s) {string.Join(", ", missing)} not available in this build " +
                    $"(available: {string.Join(", ", All.Select(k => k.Name + " [" + k.Capability + "]"))})";
                return null;
            }
            return All.Where(k => names.Contains(k.Name)).ToList();
        }
    }
}
