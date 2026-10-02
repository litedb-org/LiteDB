using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Per-thread explicit transactions through <see cref="ILiteDatabase.BeginTrans"/>,
    /// <see cref="ILiteDatabase.Commit"/> and <see cref="ILiteDatabase.Rollback"/>. A unit is
    /// either a block (BeginTrans, 1-3 collection calls, sometimes a nested BeginTrans, then Commit
    /// or Rollback) or a single Commit/Rollback issued without a transaction of its own.
    /// </summary>
    public sealed class LegacyAccess : IAccessKind
    {
        public const string KindName = "legacy";
        public const string BeginTrans = nameof(BeginTrans);
        public const string Commit = nameof(Commit);
        public const string Rollback = nameof(Rollback);

        public string Name => KindName;

        public string Capability => "per-thread-transactions";

        public int Weight => 2;

        public CommandUnit GenerateUnit(UnitGenerationContext context)
        {
            var random = context.Random;
            if (context.Budget >= 1 && random.Next(100) < 12)
            {
                // Completion without an own transaction: false, or a refusal while another thread's
                // explicit transaction is open.
                return Unit(new PropertyCommand(KindName, random.Next(2) == 0 ? Commit : Rollback));
            }
            if (context.Budget < 3) return null;

            var home = random.Next(context.Collections);
            var operations = 1 + random.Next(Math.Min(3, context.Budget - 2));
            var commands = new List<PropertyCommand> { new PropertyCommand(KindName, BeginTrans) };
            for (var i = 0; i < operations; i++)
            {
                commands.Add(DataOperations.Generate(KindName, random, context.Collections, context.Keys, home));
            }
            if (commands.Count + 2 <= context.Budget && random.Next(100) < 12)
            {
                // Nested BeginTrans returns false and joins (or starts a new transaction after an abort).
                var position = 1 + random.Next(commands.Count);
                if (context.Mode == ConnectionType.Shared && !context.IncludeKnownFindings)
                    position = 1; // KnownFindings.SharedRestartAfterAbortRetainsMutex
                commands.Insert(position, new PropertyCommand(KindName, BeginTrans));
            }
            commands.Add(new PropertyCommand(KindName, random.Next(100) < 75 ? Commit : Rollback));
            return Unit(commands.ToArray());
        }

        private static CommandUnit Unit(params PropertyCommand[] commands) => new CommandUnit(KindName, commands);

        public IEnumerable<CommandUnit> ShrinkUnit(CommandUnit unit)
        {
            var commands = unit.Commands;
            // The first and last commands of a block delimit it; anything between may go.
            for (var i = 1; i < commands.Count - 1; i++)
            {
                yield return new CommandUnit(unit.Kind, commands.Where((_, index) => index != i));
            }
            if (commands.Count > 1 && commands[commands.Count - 1].Op == Rollback)
            {
                yield return new CommandUnit(unit.Kind, commands.Take(commands.Count - 1).Concat(new[] { new PropertyCommand(KindName, Commit) }));
            }
        }

        public Observation Execute(PropertyCommand command, ThreadContext context)
        {
            if (DataOperations.IsDataOperation(command.Op))
                return DataOperations.Execute(command, context.Collection(command.Collection));
            try
            {
                switch (command.Op)
                {
                    case BeginTrans: return Observation.Ok(context.Database.BeginTrans());
                    case Commit: return Observation.Ok(context.Database.Commit());
                    case Rollback: return Observation.Ok(context.Database.Rollback());
                    default: throw new ArgumentException("Unknown legacy operation: " + command.Op);
                }
            }
            catch (Exception ex) when (!(ex is ArgumentException))
            {
                return Observation.FromException(ex);
            }
        }

        public void Apply(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes)
        {
            var isCompletion = command.Op == Commit || command.Op == Rollback;
            // A completion that does not own the Shared mutex only tries it (SharedEngine.CompleteTransaction).
            if (!isCompletion && ThreadSemantics.WaitsForSharedHolder(state, thread)) return;

            if (DataOperations.IsDataOperation(command.Op))
            {
                DataOperations.Apply(state, thread, thread, command, ThreadSemantics.AbortFor(thread), outcomes);
                return;
            }

            var next = state.Clone();
            switch (command.Op)
            {
                case BeginTrans:
                    outcomes.Add(new ModelOutcome(Observation.Ok(BeginTransaction(next, thread)), next));
                    return;
                case Commit:
                case Rollback:
                    if (state.Mode == ConnectionType.Direct && command.Op == Commit && ForeignCommitAfterScan(next, thread, outcomes)) return;
                    outcomes.Add(new ModelOutcome(state.Mode == ConnectionType.Shared
                        ? CompleteShared(next, thread, command.Op == Commit)
                        : CompleteDirect(next, thread, command.Op == Commit), next));
                    if (state.Mode == ConnectionType.Direct && command.Op == Commit) ForeignCommitScan(state, thread, outcomes);
                    return;
                default:
                    throw new ArgumentException("Unknown legacy operation: " + command.Op);
            }
        }

        /// <summary>
        /// LiteEngine.BeginTrans: one transaction per thread; a nested call joins it and returns
        /// false. Either way the thread's abort mark is consumed.
        /// </summary>
        private static bool BeginTransaction(ModelState state, int thread)
        {
            state.ConsumeAbortFlag(thread);
            if (state.Transaction(thread) != null) return false;
            state.Begin(thread, thread, isExplicit: true);
            if (state.Mode == ConnectionType.Shared) state.SharedHolder = thread;
            return true;
        }

        /// <summary>
        /// LiteEngine.Commit/Rollback via GetTransactionForCompletion: complete the thread's own
        /// transaction; otherwise Commit throws when another thread has an active explicit
        /// transaction and this thread was not just aborted; Rollback returns false. (A foreign
        /// Commit may also return false while those transactions hand over: <see cref="ForeignCommitScan"/>.)
        /// </summary>
        private static Observation CompleteDirect(ModelState state, int thread, bool commit)
        {
            var aborted = state.ConsumeAbortFlag(thread);
            var transaction = state.Transaction(thread);
            if (transaction != null)
            {
                if (commit) state.Commit(transaction);
                else state.Rollback(transaction);
                return Observation.Ok(true);
            }
            if (commit && !aborted && state.Transactions.Any(t => IsForeignExplicit(t, thread)))
                return Observation.Error(0);
            return Observation.Ok(false);
        }

        /// <summary><see cref="ModelState.Pending"/> phase of a foreign Commit between its scan and its check.</summary>
        public const int ForeignCommitPending = 2;

        /// <summary>
        /// Direct Commit without an own transaction, second outcome. The engine's refusal is not
        /// decided at one instant (TransactionCompletionGuard.cs:21-23): it first copies the
        /// registered transactions slot by slot (TransactionMonitor.cs:35,
        /// TransactionRegistry.cs:95-105, no lock), later reads each copied transaction's
        /// ExplicitTransaction and State. A transaction that begins after the copy, or ends before
        /// its check, is not seen. So Commit returns false also when foreign explicit transactions
        /// hand over during the call, and throws only for one that stays active from the scan to
        /// the check. The scan is the first point: it marks every foreign explicit transaction
        /// active there; the check (<see cref="ForeignCommitAfterScan"/>) is a later point of the
        /// same call where none of them is active any more. With no foreign explicit transaction
        /// at the scan, the single-point outcome of <see cref="CompleteDirect"/> already covers it.
        /// </summary>
        private static void ForeignCommitScan(ModelState state, int thread, List<ModelOutcome> outcomes)
        {
            if (state.Transaction(thread) != null || state.AbortFlag(thread)) return;
            if (!state.Transactions.Any(t => IsForeignExplicit(t, thread))) return;
            var next = state.Clone();
            foreach (var transaction in next.Transactions)
            {
                if (IsForeignExplicit(transaction, thread)) transaction.ScannedBy |= 1 << thread;
            }
            next.SetPending(thread, ForeignCommitPending);
            outcomes.Add(new ModelOutcome(Observation.Ok(false), next, completes: false));
        }

        /// <summary>
        /// Second point of <see cref="ForeignCommitScan"/>: returns false once every transaction the
        /// scan saw has ended; while one is still active the call cannot end here (it waits for a
        /// later position). Returns false when the thread has no pending scan.
        /// </summary>
        private static bool ForeignCommitAfterScan(ModelState next, int thread, List<ModelOutcome> outcomes)
        {
            if (next.Pending(thread) != ForeignCommitPending) return false;
            if (next.Transactions.Any(t => (t.ScannedBy & (1 << thread)) != 0)) return true;
            next.SetPending(thread, 0);
            outcomes.Add(new ModelOutcome(Observation.Ok(false), next));
            return true;
        }

        private static bool IsForeignExplicit(ModelTransaction transaction, int thread) =>
            transaction.Explicit && transaction.OwnerThread != thread;

        /// <summary>
        /// SharedEngine.CompleteTransaction: the owner completes in the engine and releases the
        /// mutex. Another thread cannot enter the mutex: Commit throws while an explicit
        /// transaction is running, Rollback returns false. With no explicit transaction running it
        /// returns false without reaching the engine (the abort mark is not consumed).
        /// </summary>
        private static Observation CompleteShared(ModelState state, int thread, bool commit)
        {
            if (state.SharedHolder == thread)
            {
                state.SharedHolder = ModelState.NoOwner;
                return CompleteDirect(state, thread, commit);
            }
            if (state.SharedHolder != ModelState.NoOwner && commit) return Observation.Error(0);
            return Observation.Ok(false);
        }
    }
}
