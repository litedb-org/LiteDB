using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LiteDB.Tests.Concurrency.ParallelProperty
{
    public static partial class HandleModel
    {
        /// <summary><see cref="ModelState.Pending"/> phase of a callback statement between its two points.</summary>
        private const int CallbackPending = 2;

        private const int CallbackPromiseBase = 7800000;
        private const int CallbackObservationBase = 7900000;

        private static readonly Observation[] InsertResults = { Observation.Ok(1), Observation.Error(LiteException.INDEX_DUPLICATE_KEY) };
        private static readonly ConcurrentDictionary<string, int> CallbackIds = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<int, Observation> CallbackValues = new ConcurrentDictionary<int, Observation>();

        /// <summary>
        /// A bulk insert on handle h whose input enumeration first runs ordinary work on the same thread
        /// (documentation: "Ordinary database collections, including ones used in a mapper/input callback, remain
        /// ordinary operations"). The statement takes the target collection's write lock, then reads its input:
        /// the callback is an automatic transaction of this thread that runs while h executes and holds that lock,
        /// so a write it makes there conflicts with h (LOCK_TIMEOUT, like any conflicting holder); the callback
        /// catches its failure. Two points: (1) lock taken and callback effect, (2) the document inserted in h; the
        /// lock is held in between, so other threads' writes may time out against it. Observation
        /// "cb=&lt;callback result&gt;;insert=&lt;1 or error&gt;", or a plain refusal / LOCK_TIMEOUT when the
        /// statement failed before reading its input. With <c>LITEDB_PBT_EXECUTING_SELF_WAIT_FAIL_FAST=1</c> (and the
        /// early-timeout split), a callback write that conflicts with the executing handle itself must fail early
        /// (API card, normative rule 3, documented only from e821ae74 on).
        /// </summary>
        private static void ApplyCallback(ModelState state, PropertyCommand command, int thread, List<ModelOutcome> outcomes, bool strictTimeouts)
        {
            if (command.Slot > 0)
            {
                ApplyCallback(state, command, command.Slot, thread, outcomes, strictTimeouts, borrowed: false);
                return;
            }
            // A callback on whatever handle is lent in the slot (sequential handoff from another thread).
            var slot = -command.Slot;
            var pending = state.Register(BorrowPendingBase + thread);
            if (state.Pending(thread) != 0 && pending != 0)
            {
                ApplyCallback(state, command, pending, thread, outcomes, strictTimeouts, borrowed: true);
                return;
            }
            if (state.Register(SlotBase + slot) == 0) outcomes.Add(new ModelOutcome(HandleObservations.Absent, state));
            for (var h = 1; h <= state.Register(MaxIdKey); h++)
            {
                if (state.Register(EverBase + slot * 10000 + h) == 1)
                    ApplyCallback(state, command, h, thread, outcomes, strictTimeouts, borrowed: true);
            }
        }

        private static void ApplyCallback(ModelState state, PropertyCommand command, int handle, int thread, List<ModelOutcome> outcomes,
            bool strictTimeouts, bool borrowed)
        {
            if (!borrowed)
            {
                ApplyCallbackOn(state, command, handle, thread, outcomes, strictTimeouts);
                return;
            }
            var produced = new List<ModelOutcome>();
            ApplyCallbackOn(state, command, handle, thread, produced, strictTimeouts);
            foreach (var outcome in produced)
            {
                // Remember which lent handle a two-point call uses (fresh states only; refusals reuse the input state).
                if (!outcome.Completes) outcome.Next.SetRegister(BorrowPendingBase + thread, handle);
                else if (outcome.Next.Register(BorrowPendingBase + thread) != 0) outcome.Next.SetRegister(BorrowPendingBase + thread, 0);
                outcomes.Add(outcome);
            }
        }

        private static void ApplyCallbackOn(ModelState state, PropertyCommand command, int handle, int thread, List<ModelOutcome> outcomes, bool strictTimeouts)
        {
            var insert = new PropertyCommand(HandleAccessKind.KindName, DataOperations.Insert, command.Collection, command.Key, command.Payload, handle);
            var abort = (Action<ModelState, ModelTransaction>)((s, t) => Abort(s, t, handle));
            if (state.Pending(thread) == CallbackPending)
            {
                SecondPoint(state, insert, thread, abort, outcomes, strictTimeouts);
                return;
            }
            var status = State(state, handle);
            if (state.Pending(thread) != 0 || status != Active)
            {
                // Second point of a lock wait, or a refusal before executing: the callback never ran.
                ApplyOnHandle(state, handle, thread, insert, outcomes, strictTimeouts, borrowed: false);
                return;
            }
            outcomes.Add(new ModelOutcome(HandleObservations.Overlap, state));
            if (!state.CanWrite(state.Transaction(OwnerBase + handle), command.Collection))
            {
                // The statement waits for the collection lock before it reads its input.
                Data(state, OwnerBase + handle, thread, insert, abort, outcomes, strictTimeouts, handle);
                return;
            }

            var locked = state.Clone();
            locked.AcquireWrite(locked.Transaction(OwnerBase + handle), command.Collection);
            var users = UsersBase + handle;
            if (strictTimeouts) locked.SetRegister(users, locked.Register(users) | (1 << thread));

            var callback = HandleAccessKind.CallbackCommand(command);
            var againstExecuting = locked.LockHolder(callback.Collection) == OwnerBase + handle && DataOperations.IsWrite(callback.Op);
            var afterCallback = new List<ModelOutcome>();
            Data(locked, thread, thread, callback, ThreadSemantics.AbortFor(thread), afterCallback, strictTimeouts, -1);
            foreach (var cb in afterCallback)
            {
                if (!cb.Completes) continue;
                if (againstExecuting && strictTimeouts && HandleAccessKind.ExecutingSelfWaitFailFast && cb.Observation.Equals(Observation.LockTimeout)) continue;
                foreach (var promise in InsertResults)
                {
                    var next = cb.Next.Clone();
                    next.SetPending(thread, CallbackPending);
                    next.SetRegister(CallbackPromiseBase + thread, Array.IndexOf(InsertResults, promise) + 1);
                    next.SetRegister(CallbackObservationBase + thread, Intern(cb.Observation));
                    outcomes.Add(new ModelOutcome(HandleObservations.Callback(cb.Observation, promise), next, completes: false));
                }
            }
        }

        private static void SecondPoint(ModelState state, PropertyCommand insert, int thread,
            Action<ModelState, ModelTransaction> abort, List<ModelOutcome> outcomes, bool strictTimeouts)
        {
            var promise = InsertResults[state.Register(CallbackPromiseBase + thread) - 1];
            var callback = CallbackValues[state.Register(CallbackObservationBase + thread)];
            var cleared = state.Clone();
            cleared.SetPending(thread, 0);
            cleared.SetRegister(CallbackPromiseBase + thread, 0);
            cleared.SetRegister(CallbackObservationBase + thread, 0);
            if (State(cleared, insert.Slot) != Active) return; // a borrowed call aborted it meanwhile (model permissiveness)
            var inserted = new List<ModelOutcome>();
            Data(cleared, OwnerBase + insert.Slot, thread, insert, abort, inserted, strictTimeouts, insert.Slot);
            foreach (var outcome in inserted)
            {
                if (!outcome.Completes) continue;
                var result = outcome.Observation.Kind == OutcomeKind.Ok ? Observation.Ok(1) : outcome.Observation;
                if (result.Equals(promise)) outcomes.Add(new ModelOutcome(HandleObservations.Callback(callback, promise), outcome.Next));
            }
        }

        private static int Intern(Observation observation)
        {
            lock (CallbackIds)
            {
                if (CallbackIds.TryGetValue(observation.ToString(), out var id)) return id;
                id = CallbackIds.Count + 1;
                CallbackIds[observation.ToString()] = id;
                CallbackValues[id] = observation;
                return id;
            }
        }
    }
}
