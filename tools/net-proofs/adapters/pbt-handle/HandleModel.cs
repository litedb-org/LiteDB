using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    /// <summary>
    /// Model of the transaction-handle access kind (<see cref="HandleAccessKind"/>), written from the
    /// handle documentation (docs/transaction-handles.md) and the public API only. Where the documentation
    /// is silent the most permissive reasonable reading is taken; every such choice is marked PERMISSIVE.
    /// <list type="number">
    /// <item>A handle is one transaction that belongs to no thread: owner key 1000 + handle number. Only
    /// collections obtained from the handle enlist; ordinary calls on the same thread are automatic
    /// transactions. Two handles are separate transactions, even on one thread (collection locks
    /// govern them like any two transactions).</item>
    /// <item>Sequential handoff to any thread is allowed. An overlapping call on the same handle is refused
    /// before executing (a no-op) and does not abort the running call. The refusal is permitted only when
    /// the harness saw another call on that handle overlap it in real time.</item>
    /// <item>A failing statement (duplicate key, lock timeout) aborts the whole handle: rollback, Failed.</item>
    /// <item>Use after completion is refused (InvalidOperationException), use after Dispose throws
    /// ObjectDisposedException. Repeated completion is a usage error, repeated Dispose is safe, Dispose of
    /// an active handle rolls it back. PERMISSIVE: Rollback of a Failed handle may also succeed.</item>
    /// <item>Shared: at most one live handle per database; a parameterless begin waits for it (and for a
    /// legacy transaction holding the connection's mutex); a TimeSpan.Zero begin may time out.
    /// PERMISSIVE: the Zero begin may time out at any point (an in-flight ordinary call holds the native
    /// mutex too), and ordinary/legacy calls of other threads are not modelled as waiting for a live
    /// handle (the explaining order may still place them after it).</item>
    /// <item>LOCK_TIMEOUT only where a conflicting holder exists (<see cref="DataOperations.Apply"/>).
    /// With <c>LITEDB_PBT_EARLY_TIMEOUT_MS</c> set, a lock timeout faster than that bound is observed as
    /// "LockTimeout:early" and permitted only when the holder is a handle that the waiting thread itself
    /// executed calls on (a self-wait: waiting could never succeed from that thread's view). With
    /// <c>LITEDB_PBT_SELF_WAIT_FAIL_FAST=1</c> as well, a lock timeout whose holder is a handle that only the
    /// waiting thread ever executed on must be early (API card, refusal table: a collection write lock held by
    /// another owner on the same thread fails with LOCK_TIMEOUT immediately). Not a documentation rule at the
    /// handle commits; added as a second attempt, see the adapter README.</item>
    /// </list>
    /// </summary>
    public static partial class HandleModel
    {
        public const int OwnerBase = 1000;
        public const int MaxSlots = 2;

        public const int None = 0, Active = 1, Failed = 2, Completed = 3, Disposed = 4;

        private const int StateBase = 7000000;
        private const int SlotBase = 7200000;
        private const int EverBase = 7300000;
        private const int MaxIdKey = 7400000;
        private const int BorrowPendingBase = 7500000;
        private const int SharedHandleKey = 7600000;
        private const int UsersBase = 7700000;

        public static int State(ModelState state, int handle) => state.Register(StateBase + handle);

        private static void SetState(ModelState state, int handle, int value) => state.SetRegister(StateBase + handle, value);

        public static void Apply(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes, bool strictTimeouts)
        {
            var op = command.Op;
            if (HandleAccessKind.IsBegin(op))
            {
                ApplyBegin(state, command, thread, outcomes);
                return;
            }
            if (op == HandleAccessKind.Pause)
            {
                outcomes.Add(new ModelOutcome(HandleObservations.Paused, state));
                return;
            }
            if (op == HandleAccessKind.Commit || op == HandleAccessKind.Rollback || op == HandleAccessKind.Dispose)
            {
                ApplyCompletion(state, command, thread, outcomes);
                return;
            }
            if (HandleAccessKind.IsCallback(op))
            {
                ApplyCallback(state, command, thread, outcomes, strictTimeouts);
                return;
            }
            if (command.Slot == 0)
            {
                // An ordinary call made inside a handle unit: an automatic transaction of this thread.
                if (ThreadSemantics.WaitsForSharedHolder(state, thread)) return;
                Data(state, thread, thread, command, ThreadSemantics.AbortFor(thread), outcomes, strictTimeouts, -1);
                return;
            }
            if (command.Slot > 0)
            {
                ApplyOnHandle(state, command.Slot, thread, command, outcomes, strictTimeouts, borrowed: false);
                return;
            }

            var slot = -command.Slot;
            var pending = state.Register(BorrowPendingBase + thread);
            if (state.Pending(thread) != 0 && pending != 0)
            {
                ApplyOnHandle(state, pending, thread, command, outcomes, strictTimeouts, borrowed: true);
                return;
            }
            if (state.Register(SlotBase + slot) == 0) outcomes.Add(new ModelOutcome(HandleObservations.Absent, state));
            for (var h = 1; h <= state.Register(MaxIdKey); h++)
            {
                if (state.Register(EverBase + slot * 10000 + h) == 1)
                    ApplyOnHandle(state, h, thread, command, outcomes, strictTimeouts, borrowed: true);
            }
        }

        private static void ApplyBegin(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes)
        {
            var handle = command.Slot;
            var bounded = HandleAccessKind.IsBoundedBegin(command.Op);
            if (state.Mode == ConnectionType.Shared)
            {
                // PERMISSIVE: a Zero-budget begin may find the native mutex held by an in-flight call.
                if (bounded) outcomes.Add(new ModelOutcome(HandleObservations.AdmissionTimeout, state));
                if (state.Register(SharedHandleKey) != 0 || state.SharedHolder != ModelState.NoOwner) return; // waits
            }
            var next = state.Clone();
            next.Begin(OwnerBase + handle, thread, isExplicit: false);
            SetState(next, handle, Active);
            next.SetRegister(MaxIdKey, Math.Max(next.Register(MaxIdKey), handle));
            if (state.Mode == ConnectionType.Shared) next.SetRegister(SharedHandleKey, handle);
            var slot = HandleAccessKind.LendSlot(command.Op);
            if (slot > 0)
            {
                next.SetRegister(SlotBase + slot, handle);
                next.SetRegister(EverBase + slot * 10000 + handle, 1);
            }
            outcomes.Add(new ModelOutcome(HandleObservations.Begun, next));
        }

        private static void ApplyOnHandle(ModelState state, int handle, int thread, PropertyCommand command,
            List<ModelOutcome> outcomes, bool strictTimeouts, bool borrowed)
        {
            var status = State(state, handle);
            if (status == None)
            {
                outcomes.Add(new ModelOutcome(HandleObservations.Absent, state));
                return;
            }
            // Refused overlap is a no-op in every state (the overlap guard runs first).
            if (state.Pending(thread) == 0) outcomes.Add(new ModelOutcome(HandleObservations.Overlap, state));
            if (status == Failed || status == Completed)
            {
                if (state.Pending(thread) == 0) outcomes.Add(new ModelOutcome(HandleObservations.InvalidOperation, state));
                return;
            }
            if (status == Disposed)
            {
                if (state.Pending(thread) == 0) outcomes.Add(new ModelOutcome(HandleObservations.ObjectDisposed, state));
                return;
            }

            var produced = new List<ModelOutcome>();
            Data(state, OwnerBase + handle, thread, command, (s, t) => Abort(s, t, handle), produced, strictTimeouts, handle);
            foreach (var outcome in produced)
            {
                if (borrowed) outcome.Next.SetRegister(BorrowPendingBase + thread, outcome.Completes ? 0 : handle);
                outcomes.Add(outcome);
            }
        }

        /// <summary>Statement failure: the handle's transaction rolls back and the handle is Failed.</summary>
        private static void Abort(ModelState state, ModelTransaction transaction, int handle)
        {
            state.Rollback(transaction);
            SetState(state, handle, Failed);
            if (state.Register(SharedHandleKey) == handle) state.SetRegister(SharedHandleKey, 0);
        }

        private static void Data(ModelState state, int owner, int thread, PropertyCommand command,
            Action<ModelState, ModelTransaction> abort, List<ModelOutcome> outcomes, bool strictTimeouts, int executedHandle)
        {
            var baseCommand = DataOperations.Relabel(command, OrdinaryAccess.KindName);
            var produced = new List<ModelOutcome>();
            DataOperations.Apply(state, owner, thread, baseCommand, abort, produced);
            var secondPoint = state.Pending(thread) != 0;
            var selfWait = secondPoint || IsSelfWait(state, command.Collection, thread);
            // Fail-fast option: the holder is a handle only this thread ever executed on, so the wait can never
            // succeed and the API card's refusal table says the lock timeout comes immediately.
            var mustBeEarly = strictTimeouts && HandleAccessKind.SelfWaitFailFast && !secondPoint && IsSoleSelfWait(state, command.Collection, thread);
            foreach (var outcome in produced)
            {
                if (strictTimeouts && executedHandle > 0)
                {
                    var users = UsersBase + executedHandle;
                    outcome.Next.SetRegister(users, outcome.Next.Register(users) | (1 << thread));
                }
                if (!(mustBeEarly && outcome.Observation.Equals(Observation.LockTimeout))) outcomes.Add(outcome);
                if (strictTimeouts && selfWait && outcome.Observation.Equals(Observation.LockTimeout))
                    outcomes.Add(new ModelOutcome(HandleObservations.EarlyLockTimeout, outcome.Next, outcome.Completes));
            }
        }

        /// <summary>The collection's lock holder is a handle that no thread but this one executed calls on.</summary>
        private static bool IsSoleSelfWait(ModelState state, int collection, int thread)
        {
            var holder = state.LockHolder(collection);
            return holder >= OwnerBase && state.Register(UsersBase + holder - OwnerBase) == 1 << thread;
        }

        /// <summary>The collection's lock holder is a handle this thread executed calls on.</summary>
        private static bool IsSelfWait(ModelState state, int collection, int thread)
        {
            var holder = state.LockHolder(collection);
            if (holder == ModelState.NoOwner) return false;
            if (holder == thread) return true;
            return holder >= OwnerBase && (state.Register(UsersBase + holder - OwnerBase) & (1 << thread)) != 0;
        }

        private static void ApplyCompletion(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes)
        {
            var handle = command.Slot;
            var status = State(state, handle);
            if (status == None)
            {
                outcomes.Add(new ModelOutcome(HandleObservations.Absent, Unlend(state.Clone(), handle)));
                return;
            }
            // The harness retries refused overlaps; one that persisted leaves the handle as it was.
            outcomes.Add(new ModelOutcome(HandleObservations.Overlap, Unlend(state.Clone(), handle)));
            outcomes.Add(new ModelOutcome(HandleObservations.ReadersOpen, Unlend(state.Clone(), handle)));

            var next = Unlend(state.Clone(), handle);
            var op = command.Op;
            if (status == Active)
            {
                var transaction = next.Transaction(OwnerBase + handle);
                if (op == HandleAccessKind.Commit) next.Commit(transaction);
                else next.Rollback(transaction);
                SetState(next, handle, op == HandleAccessKind.Dispose ? Disposed : Completed);
                if (next.Register(SharedHandleKey) == handle) next.SetRegister(SharedHandleKey, 0);
                outcomes.Add(new ModelOutcome(HandleObservations.Completion(op), next));
                return;
            }
            if (op == HandleAccessKind.Dispose)
            {
                SetState(next, handle, Disposed);
                outcomes.Add(new ModelOutcome(HandleObservations.Completion(op), next));
                return;
            }
            if (status == Disposed)
            {
                outcomes.Add(new ModelOutcome(HandleObservations.ObjectDisposed, next));
                return;
            }
            outcomes.Add(new ModelOutcome(HandleObservations.InvalidOperation, next));
            // PERMISSIVE: the documentation does not say whether Rollback of a Failed handle is a usage error.
            if (status == Failed && op == HandleAccessKind.Rollback)
                outcomes.Add(new ModelOutcome(HandleObservations.Completion(op), next));
        }

        /// <summary>The harness stops lending a handle once its unit completed it (whatever the outcome).</summary>
        private static ModelState Unlend(ModelState state, int handle)
        {
            for (var slot = 1; slot <= MaxSlots; slot++)
            {
                if (state.Register(SlotBase + slot) == handle) state.SetRegister(SlotBase + slot, 0);
            }
            return state;
        }
    }
}
