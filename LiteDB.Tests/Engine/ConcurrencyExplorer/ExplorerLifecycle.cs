using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB.ConcurrencyTesting
{
    internal sealed class ExplorerLifecycle
    {
        private readonly ExplorerSchedule _schedule;
        private readonly ExplorerDatabase _model;
        private readonly List<IDisposable> _resources;
        private readonly ExplorerSchedule.Actor _a, _b, _c;

        internal ExplorerLifecycle(ExplorerSchedule schedule, ExplorerDatabase model, List<IDisposable> resources,
            ExplorerSchedule.Actor a, ExplorerSchedule.Actor b, ExplorerSchedule.Actor c)
        { _schedule = schedule; _model = model; _resources = resources; _a = a; _b = b; _c = c; }
        private T Keep<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
        private static object Field(object instance, string name) => ExplorerDatabase.Field(instance, name);
        private static IEnumerable<BsonDocument> Input(ExplorerSchedule.Boundary hold, Action inside = null)
        { yield return ExplorerDatabase.Row(2, 20); inside?.Invoke(); hold.Hit(); yield return ExplorerDatabase.Row(3, 30); }

        /// <summary>
        /// Close the facade while one of its handles executes a call on another thread. Close
        /// is unbounded: it waits for the executing call to return, then rolls the handle back.
        /// <paramref name="closeWaiting"/>: observe the handle's own close wait, not only the
        /// facade's closing flag, before the overlap probe and release.
        /// </summary>
        internal void CloseActive(bool closeWaiting, int seed)
        {
            var db = Keep(_model.Open());
            ILiteTransaction tx = null;
            _a.Run("begin-close-owner", () => tx = db.BeginTransaction());
            Keep(tx);
            var input = _schedule.NewBoundary("active callback before close");
            var active = _a.Invoke("active-insert", () => tx.GetCollection("rows").Insert(Input(input)));
            input.Wait();
            var close = _b.Invoke("dispose-active-session", db.Dispose);
            var handles = Field(db, "_transactionHandles");
            _schedule.Until(() => { lock (Field(handles, "_gate")) return (bool)Field(handles, "_closed"); }, "facade closing published");
            if (closeWaiting)
                _schedule.Until(() => { lock (Field(tx, "_gate")) return (bool)Field(tx, "_closing"); }, "close waits for the executing call");
            _c.Run("overlap-during-close", () =>
            {
                // Once close refuses new calls, a call from another thread reports the close;
                // before that (only when close may not wait yet) it is refused as an overlap.
                Exception refusal = null;
                try { if ((seed & 1) == 0) tx.Commit(); else tx.Rollback(); }
                catch (Exception error) { refusal = error; }
                var overlap = refusal?.GetType() == typeof(InvalidOperationException) && refusal.Message == ExplorerDatabase.OverlapRefusal;
                ExplorerDatabase.Require(refusal is ObjectDisposedException || (!closeWaiting && overlap),
                    "completion during close was not refused: " + (refusal?.GetType().Name ?? "accepted"));
                ExplorerDatabase.Require(tx.State == LiteTransactionState.Active, "overlap during close changed active transaction outcome");
                ExplorerDatabase.Require(!close.Done.IsSet, "close returned while a handle call was executing");
            });
            input.Release();
            _a.Complete(active); _b.Complete(close);
            ExplorerDatabase.Require(tx.State == LiteTransactionState.RolledBack, "close did not roll back active handle");
            _c.Run("use-after-close", () => ExplorerDatabase.RefusedAfterClose(tx.Commit, "handle commit"));
        }

        /// <summary>
        /// A legacy transaction is closed under its owner thread by another thread while a
        /// writer waits for it. Direct keeps dev's semantics: the foreign close does not
        /// release the owner thread's collection lock, so the waiter times out and nothing
        /// is written after close. Shared hands writer ownership to the native waiter.
        /// </summary>
        internal void LegacyClose(int seed)
        {
            // A lock waiter uses the timeout its engine opened with: persist it, then reopen.
            if (!_model.Shared) using (var setup = _model.Open()) { setup.Timeout = TimeSpan.FromSeconds(1); setup.Checkpoint(); }
            var owner = Keep(new LiteDatabase(_model.Connection));
            var waiter = _model.Shared ? Keep(_model.Open()) : owner;
            _a.Run("legacy-owner-write", () =>
            {
#pragma warning disable CS0618 // The legacy thread-bound API is the subject of this schedule.
                ExplorerDatabase.Require(owner.BeginTrans(), "legacy begin failed");
#pragma warning restore CS0618
                owner.GetCollection("rows").Insert(ExplorerDatabase.Row(2, 20));
            });
            var waits = _schedule.NewBoundary(_model.Shared ? "waiter blocks on native ownership" : "waiter blocks on rows lock");
            CollectionLock rows = null;
            SharedMutexTurnstile turnstile = null;
            if (_model.Shared)
            {
                turnstile = (SharedMutexTurnstile)Field(ExplorerDatabase.EngineOf(waiter), "_turnstile");
                turnstile.BeforeMainWait = waits.Observe;
            }
            else
            {
                var locker = Field(ExplorerDatabase.EngineOf(owner), "_locker");
                ExplorerDatabase.Require(((EnginePragmas)Field(locker, "_pragmas")).Timeout == TimeSpan.FromSeconds(1), "lock timeout fixture not effective");
                var locks = (ConcurrentDictionary<string, CollectionLock>)Field(locker, "_collections");
                rows = locks["rows"];
                rows.BeforeWait = waits.Observe;
            }
            Exception outcome = null;
            var write = _c.Invoke("waiting-writer", () =>
            {
                try { waiter.GetCollection("rows").Insert(ExplorerDatabase.Row(3, 30)); }
                catch (LiteException error) when (!_model.Shared) { outcome = error; }
            });
            waits.Wait();
            _b.Run("foreign-close", owner.Dispose);
            _c.Complete(write);
            if (rows != null) rows.BeforeWait = null;
            if (turnstile != null) turnstile.BeforeMainWait = null;
            if (_model.Shared) _model.Acknowledge("rows", 3, 30);
            else ExplorerDatabase.Require((outcome as LiteException)?.ErrorCode == LiteException.LOCK_TIMEOUT,
                "waiter for a foreign-closed legacy owner did not time out as on dev: " + outcome);
#pragma warning disable CS0618
            _a.Run("legacy-complete-after-close", () =>
            {
                // Refused, or reported as nothing to complete; never a success after close.
                bool completed;
                try { completed = (seed & 1) == 0 ? owner.Commit() : owner.Rollback(); }
                catch (ObjectDisposedException) { return; }
                catch (LiteException) { return; }
                ExplorerDatabase.Require(!completed, "legacy completion was accepted after close");
            });
#pragma warning restore CS0618
        }

        /// <summary>
        /// Direct maintenance queued behind a writer. An ordinary writer's callback holds its
        /// lease; a handle's callback runs while the handle's lease blocks the rebuild, and its
        /// ordinary read must not queue behind that rebuild. A late reader is fenced by it.
        /// </summary>
        internal void Maintenance(bool handleCallback, int seed)
        {
            var db = Keep(_model.Open());
            var gate = Field(Field(ExplorerDatabase.EngineOf(db), "_locker"), "_transaction");
            var inside = _schedule.NewBoundary(handleCallback ? "handle write callback" : "ordinary write callback under lease");
            ILiteTransaction tx = null;
            ExplorerSchedule.Work active = null, maintenance;
            var commit = !handleCallback || (seed & 1) == 0;
            var callbackRead = false;
            if (handleCallback)
            {
                _a.Run("begin", () => tx = db.BeginTransaction());
                Keep(tx);
                _a.Run("hold-handle-lease", () => tx.GetCollection("other").Insert(ExplorerDatabase.Row(5, 50)));
            }
            else active = _a.Invoke("ordinary-insert", () => db.GetCollection("rows").Insert(Input(inside)));
            if (!handleCallback) inside.Wait();
            maintenance = _b.Invoke("rebuild", () => db.Rebuild());
            _schedule.Until(() => { lock (Field(gate, "_sync")) return (int)Field(gate, "_waitingWriters") != 0; },
                "maintenance queued behind the writer's lease");
            var late = _c.Invoke("late-read", () =>
            {
                // Rebuild closes the gate a queued reader waits in: as on dev, that reader may fail
                // with EngineDisposed. If it reads, it must see the complete committed state.
                try
                {
                    ExplorerDatabase.Require(db.GetCollection("rows").Count() == (commit ? 3 : 1),
                        "late reader saw incomplete or uncommitted state");
                }
                catch (LiteException error) when (error.ErrorCode == LiteException.ENGINE_DISPOSED)
                { _schedule.Event("late reader queued in the replaced gate: engine disposed"); }
            });
            if (handleCallback)
            {
                active = _a.Invoke("handle-insert", () => tx.GetCollection("rows").Insert(Input(inside, () =>
                {
                    // The queued writer waits for this handle: this read must not wait for it.
                    ExplorerDatabase.Require(db.GetCollection("rows").FindById(2) == null, "callback read saw uncommitted row");
                    ExplorerDatabase.Require(!maintenance.Done.IsSet, "maintenance did not wait for the open handle");
                    callbackRead = true;
                })));
                inside.Wait();
                ExplorerDatabase.Require(callbackRead, "callback read did not complete before release");
            }
            ExplorerDatabase.Require(!maintenance.Done.IsSet && !late.Done.IsSet,
                "maintenance did not fence the observed active/queued operations");
            inside.Release();
            _a.Complete(active);
            if (handleCallback) _a.Run(commit ? "commit" : "rollback", commit ? (Action)tx.Commit : tx.Rollback);
            _b.Complete(maintenance); _c.Complete(late);
            if (commit)
            {
                _model.Acknowledge("rows", 2, 20); _model.Acknowledge("rows", 3, 30);
                if (handleCallback) _model.Acknowledge("other", 5, 50);
            }
            if (!handleCallback && (seed & 1) != 0) _b.Run("checkpoint-after-rebuild", () => db.Checkpoint());
        }
    }
}
