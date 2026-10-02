using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using LiteDB.Engine;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>Direct: two handles wait for each other's collection lock; at least one times out and fails.</summary>
    internal static class ExplorerCollectionCycle
    {
        internal static void Run(ExplorerSchedule schedule, ExplorerDatabase model, List<IDisposable> resources,
            ExplorerSchedule.Actor a, ExplorerSchedule.Actor b, bool reverse)
        {
            // Some pragma consumers retain their opening generation. Persist and reopen
            // the fixture before measuring its one-second collection-lock contract.
            using (var setup = model.Open()) { setup.Timeout = TimeSpan.FromSeconds(1); setup.Checkpoint(); }
            var db = new LiteDatabase(model.Connection); resources.Add(db);
            ExplorerDatabase.Require(db.Timeout == TimeSpan.FromSeconds(1), "C04 timeout fixture not persisted");
            ILiteTransaction first = null, second = null;
            a.Run("C04 begin A", () => first = db.BeginTransaction());
            b.Run("C04 begin B", () => second = db.BeginTransaction());
            resources.Add(first); resources.Add(second);
            a.Run("C04 hold rows", () => first.GetCollection("rows").Insert(ExplorerDatabase.Row(2, 20)));
            b.Run("C04 hold other", () => second.GetCollection("other").Insert(ExplorerDatabase.Row(4, 40)));
            var locker = ExplorerDatabase.Field(ExplorerDatabase.EngineOf(db), "_locker");
            var effective = (EnginePragmas)ExplorerDatabase.Field(locker, "_pragmas");
            ExplorerDatabase.Require(effective.Timeout == TimeSpan.FromSeconds(1), "C04 effective lock timeout differs from fixture");
            var locks = (ConcurrentDictionary<string, CollectionLock>)ExplorerDatabase.Field(locker, "_collections");
            var waitsRows = schedule.NewBoundary("C04 actual wait rows held by A");
            var waitsOther = schedule.NewBoundary("C04 actual wait other held by B");
            locks["rows"].BeforeWait = waitsRows.Observe;
            locks["other"].BeforeWait = waitsOther.Observe;
            var failures = new LiteException[2];
            Action<ILiteTransaction, string, int, int> cross = (tx, collection, id, owner) =>
            {
                try { tx.GetCollection(collection).Insert(ExplorerDatabase.Row(id, 30)); }
                catch (LiteException error)
                {
                    ExplorerDatabase.Require(error.ErrorCode == LiteException.LOCK_TIMEOUT, "unexpected C04 error " + error);
                    failures[owner] = error;
                }
            };
            ExplorerSchedule.Work aw, bw;
            if (reverse)
            {
                bw = b.Invoke("C04 B waits rows", () => cross(second, "rows", 5, 1));
                aw = a.Invoke("C04 A waits other", () => cross(first, "other", 3, 0));
            }
            else
            {
                aw = a.Invoke("C04 A waits other", () => cross(first, "other", 3, 0));
                bw = b.Invoke("C04 B waits rows", () => cross(second, "rows", 5, 1));
            }
            waitsRows.Wait(); waitsOther.Wait();
            a.Complete(aw); b.Complete(bw);
            ExplorerDatabase.Require(failures[0] != null || failures[1] != null, "cyclic writers both escaped without contention outcome");
            if (failures[0] == null)
            {
                a.Run("C04 commit A", first.Commit);
                model.Acknowledge("rows", 2, 20); model.Acknowledge("other", 3, 30);
            }
            else ExplorerDatabase.Require(first.State == LiteTransactionState.Failed, "losing A transaction not aborted");
            if (failures[1] == null)
            {
                b.Run("C04 commit B", second.Commit);
                model.Acknowledge("other", 4, 40); model.Acknowledge("rows", 5, 30);
            }
            else ExplorerDatabase.Require(second.State == LiteTransactionState.Failed, "losing B transaction not aborted");
            locks["rows"].BeforeWait = null; locks["other"].BeforeWait = null;
        }
    }
}
