using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Shared: a parameterless begin that must wait for the writer ownership of another
    /// owner. A handle owner makes it wait in the process-local handle queue; a legacy owner
    /// makes its holder wait for the native mutex. The waiter is then either admitted after
    /// the owner completes, or its facade closes first (the begin fails, nothing commits).
    /// </summary>
    internal static class ExplorerAdmission
    {
        private const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic;

        internal static void Run(ExplorerSchedule schedule, ExplorerDatabase model, List<IDisposable> resources,
            ExplorerSchedule.Actor a, ExplorerSchedule.Actor b, ExplorerSchedule.Actor c,
            bool legacy, bool closing, bool usePeer, int seed)
        {
            var owner = model.Open(); resources.Add(owner);
            var waiter = owner;
            if (usePeer) { waiter = model.Open(); resources.Add(waiter); }
            var engine = (SharedEngine)ExplorerDatabase.EngineOf(owner);
            ILiteTransaction tx = null;
            a.Run("owner-begin-write", () =>
            {
                if (legacy)
                {
#pragma warning disable CS0618 // The legacy thread-bound API is the contended owner here.
                    ExplorerDatabase.Require(owner.BeginTrans(), "legacy begin failed");
#pragma warning restore CS0618
                    owner.GetCollection("rows").Insert(ExplorerDatabase.Row(2, 20));
                }
                else { tx = owner.BeginTransaction(); tx.GetCollection("rows").Insert(ExplorerDatabase.Row(2, 20)); }
            });
            if (tx != null) resources.Add(tx);
            Exception refusal = null;
            var pending = b.Invoke("contended-begin", () =>
            {
                ILiteTransaction next;
                try { next = waiter.BeginTransaction(); }
                catch (ObjectDisposedException error) when (closing) { refusal = error; return; }
                using (next)
                {
                    ExplorerDatabase.Require(!closing, "begin on a closed facade was admitted");
                    next.GetCollection("other").Insert(ExplorerDatabase.Row(2, 20));
                    next.Commit();
                }
            });
            // The holder's child engine and turnstile only exist inside the begin, so the native
            // stage is observed through the shared turnstile, the local stage through the queue.
            if (legacy)
            {
                var turnstile = (SharedMutexTurnstile)ExplorerDatabase.Field(engine, "_turnstile");
                schedule.Until(turnstile.HasWaiter, "begin holder queued for native writer ownership");
            }
            else
            {
                var name = (string)ExplorerDatabase.Field(engine, "_mutexName");
                var writers = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(SharedEngine)
                    .GetField("TransactionWriters", Statics).GetValue(null);
                var queue = writers[name];
                schedule.Until(() => queue.CurrentCount == 0 && Waiters(queue) > 0, "begin queued behind the open handle");
            }
            ExplorerDatabase.Require(!pending.Done.IsSet, "contended begin already finished");
            var commit = (seed & 1) == 0;
            if (closing && !usePeer)
            {
                // Closing the owner's facade rolls back its owner; the queued begin is not yet
                // admitted to the facade and fails once it gets ownership.
                c.Run("close-owner-session", owner.Dispose);
                b.Complete(pending);
                if (tx != null) ExplorerDatabase.Require(tx.State == LiteTransactionState.RolledBack, "close did not roll back owner handle");
            }
            else
            {
                if (closing) c.Run("close-waiter", waiter.Dispose);
                a.Run(commit ? "owner-commit" : "owner-rollback", () =>
                {
#pragma warning disable CS0618
                    if (legacy) ExplorerDatabase.Require(commit ? owner.Commit() : owner.Rollback(), "legacy completion failed");
#pragma warning restore CS0618
                    else if (commit) tx.Commit();
                    else tx.Rollback();
                });
                if (commit) model.Acknowledge("rows", 2, 20);
                b.Complete(pending);
                if (!closing) model.Acknowledge("other", 2, 20);
            }
            ExplorerDatabase.Require(closing == (refusal != null), "closing waiter outcome");
        }

        /// <summary>Threads blocked in the semaphore; <c>CurrentCount</c> alone cannot tell.</summary>
        private static int Waiters(SemaphoreSlim queue)
        {
            var field = typeof(SemaphoreSlim).GetField("m_waitCount", BindingFlags.Instance | BindingFlags.NonPublic);
            // Fall back to the weaker queue-held condition on a runtime without the field.
            return field == null ? 1 : (int)field.GetValue(queue);
        }
    }
}
