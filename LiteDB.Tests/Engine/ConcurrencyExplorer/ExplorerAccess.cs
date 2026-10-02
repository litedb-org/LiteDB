using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// An ACCESS KIND: how an actor groups operations on one connection into a unit of work.
    /// Adapters are discovered by reflection (<see cref="ExplorerAccessKinds"/>), so a historical
    /// adapter copied into a fork tree (tools/net-proofs/adapters/transaction-handle) registers
    /// itself without changing this file.
    /// </summary>
    internal interface IExplorerAccess
    {
        /// <summary>Stable, unique name (ordinary, legacy, handle); appears in signatures.</summary>
        string Name { get; }

        /// <summary>The net-proof capability the library must provide (M0 names: legacy-transactions, handle-api).</summary>
        string Capability { get; }

        /// <summary>True when a unit must be completed on the thread that began it.</summary>
        bool ThreadAffine { get; }

        /// <summary>True when a unit spans operations until Commit/Rollback (false: each operation commits itself).</summary>
        bool Transactional { get; }

        /// <summary>Begins a unit of work on the calling (actor) thread.</summary>
        IExplorerUnit Begin(LiteDatabase connection);
    }

    /// <summary>One unit of work. Effects are acknowledged when <see cref="Transactional"/> is false
    /// and the operation returned, or when <see cref="Commit"/> returned true.</summary>
    internal interface IExplorerUnit : IDisposable
    {
        bool Transactional { get; }

        ILiteCollection<BsonDocument> Collection(string name);

        /// <summary>True when the unit's effects are now committed; false when there was nothing left to commit
        /// (a failed operation already rolled the unit back).</summary>
        bool Commit();

        void Rollback();
    }

    /// <summary>Upstream adapter, kind <c>ordinary</c>: every operation is its own auto-commit transaction.</summary>
    internal sealed class OrdinaryAccess : IExplorerAccess
    {
        public string Name => "ordinary";
        public string Capability => "legacy-transactions";
        public bool ThreadAffine => false;
        public bool Transactional => false;
        public IExplorerUnit Begin(LiteDatabase connection) => new Unit(connection);

        private sealed class Unit : IExplorerUnit
        {
            private readonly LiteDatabase _db;
            internal Unit(LiteDatabase db) { _db = db; }
            public bool Transactional => false;
            public ILiteCollection<BsonDocument> Collection(string name) => _db.GetCollection(name);
            public bool Commit() => true;
            public void Rollback() { }
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Upstream adapter, kind <c>legacy</c>: <c>BeginTrans</c> / <c>Commit</c> / <c>Rollback</c> on the
    /// connection; thread-affine (docs/explicit-transactions.md: begin, operations and completion run
    /// on one thread).
    /// </summary>
    internal sealed class LegacyAccess : IExplorerAccess
    {
        public string Name => "legacy";
        public string Capability => "legacy-transactions";
        public bool ThreadAffine => true;
        public bool Transactional => true;

        public IExplorerUnit Begin(LiteDatabase connection)
        {
            if (!connection.BeginTrans()) throw new ExplorerFailure("EXPLORER_LEGACY_BEGIN_JOINED", "BeginTrans joined an existing transaction");
            return new Unit(connection);
        }

        private sealed class Unit : IExplorerUnit
        {
            private readonly LiteDatabase _db;
            private bool _completed;
            internal Unit(LiteDatabase db) { _db = db; }
            public bool Transactional => true;
            public ILiteCollection<BsonDocument> Collection(string name) => _db.GetCollection(name);

            public bool Commit()
            {
                _completed = true;
                return _db.Commit();
            }

            public void Rollback()
            {
                _completed = true;
                _db.Rollback();
            }

            public void Dispose()
            {
                if (!_completed) this.Rollback();
            }
        }
    }

    /// <summary>Verdict of one explorer request; NotApplicable is never a pass (mirrors M5's PropertyVerdict).</summary>
    internal enum ExplorerVerdict { Passed, Failed, NotApplicable }

    /// <summary>Access kinds available in this build, discovered by reflection.</summary>
    internal static class ExplorerAccessKinds
    {
        /// <summary>Capabilities of kinds whose adapter is not part of every build.</summary>
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ordinary"] = "legacy-transactions",
            ["legacy"] = "legacy-transactions",
            ["handle"] = "handle-api"
        };

        private static readonly Lazy<IExplorerAccess[]> Discovered = new Lazy<IExplorerAccess[]>(() =>
            typeof(IExplorerAccess).Assembly.GetTypes()
                .Where(type => typeof(IExplorerAccess).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface &&
                    type.GetConstructor(Type.EmptyTypes) != null)
                .Select(type => (IExplorerAccess)Activator.CreateInstance(type))
                .OrderBy(access => access.Name, StringComparer.Ordinal).ToArray());

        public static IReadOnlyList<IExplorerAccess> All => Discovered.Value;

        /// <summary>The upstream kinds every build has.</summary>
        public static readonly string[] Upstream = { "ordinary", "legacy" };

        public static IExplorerAccess Find(string name) => All.FirstOrDefault(access => access.Name == name);

        /// <summary>Why <paramref name="name"/> cannot run in this build, or null when it can.</summary>
        public static string NotApplicableReason(string name)
        {
            if (Find(name) != null) return null;
            var capability = Known.TryGetValue(name ?? "", out var known) ? known : "unknown";
            return $"access kind '{name}' is not applicable to this build: it requires capability '{capability}', which this " +
                "library revision does not provide (the historical handle adapter in tools/net-proofs/adapters/transaction-handle " +
                "compiles only when net_proof.py copies it into a fork revision that has BeginTransaction/ILiteTransaction)";
        }
    }
}
