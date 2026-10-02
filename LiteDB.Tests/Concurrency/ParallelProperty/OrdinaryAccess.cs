using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Single collection calls, each in its own automatic transaction (or, inside a legacy block of
    /// the same thread, joined to that thread's explicit transaction).
    /// </summary>
    public sealed class OrdinaryAccess : IAccessKind
    {
        public const string KindName = "ordinary";

        public string Name => KindName;

        public string Capability => "collection-api";

        public int Weight => 3;

        public CommandUnit GenerateUnit(UnitGenerationContext context)
        {
            if (context.Budget < 1) return null;
            var command = DataOperations.Generate(KindName, context.Random, context.Collections, context.Keys);
            return new CommandUnit(KindName, new[] { command });
        }

        public IEnumerable<CommandUnit> ShrinkUnit(CommandUnit unit) => Enumerable.Empty<CommandUnit>();

        public Observation Execute(PropertyCommand command, ThreadContext context) =>
            DataOperations.Execute(command, context.Collection(command.Collection));

        public void Apply(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes)
        {
            if (ThreadSemantics.WaitsForSharedHolder(state, thread)) return;
            DataOperations.Apply(state, thread, thread, command, ThreadSemantics.AbortFor(thread), outcomes);
        }
    }

    /// <summary>Semantics shared by the thread-affine kinds (ordinary and legacy).</summary>
    public static class ThreadSemantics
    {
        /// <summary>
        /// Shared mode: an explicit transaction keeps the connection's named mutex from BeginTrans
        /// until Commit/Rollback, so every call of another thread waits for it, without the TIMEOUT
        /// pragma bounding that wait (SharedEngine.BeginTransCore keeps the recursion it opened;
        /// SharedMutexOwner.Enter polls without a deadline).
        /// </summary>
        public static bool WaitsForSharedHolder(ModelState state, int thread) =>
            state.Mode == ConnectionType.Shared && state.SharedHolder != ModelState.NoOwner && state.SharedHolder != thread;

        /// <summary>
        /// A failing operation rolls back the thread's transaction and, when it was explicit, marks the
        /// thread so its next Commit/Rollback/BeginTrans knows (LiteEngine.ExecuteAutoTransaction).
        /// </summary>
        public static Action<ModelState, ModelTransaction> AbortFor(int thread) => (state, transaction) =>
        {
            state.Rollback(transaction);
            if (transaction.Explicit) state.SetAbortFlag(thread, true);
        };
    }
}
