using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Two units of work contend: A's unit writes through an input sequence and is paused inside
    /// its callback; B begins its own unit (on a second connection to the same file in Shared mode,
    /// or the same connection from another thread), writes the same or the other collection and
    /// commits, while the maintenance contender and a read run in one of six orders. A is released,
    /// finishes (commit or rollback by the seed bit), then every contender completes. Permitted:
    /// what the configuration races, and for B a timed-out wait (LOCK_TIMEOUT) whose unit then
    /// rolls back; both units' acknowledged effects survive, and an aborted unit leaves nothing.
    /// </summary>
    internal sealed class TransactionContentionScenario : IExplorerScenario
    {
        private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(150);

        public string Name => "transaction-contention";
        public string Description => "Two units of work on one file contend while one is paused in its input callback.";
        public int Variants => 6 * 2 * 2;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) => null;

        public void Run(ExplorerRun run, int variant)
        {
            var order = ExplorerRun.Permutations[variant % 6];
            var sameCollection = variant / 6 % 2 == 0;
            var separateConnection = variant / 12 == 1 && run.Configuration.Shared;
            var c = run.Configuration;
            var disturbance = CallbackPauseScenario.Disturbance(c);
            var db = run.Open();
            var db2 = separateConnection ? run.Open() : db;
            var peer = c.Callback == ExplorerCallback.Peer ? run.Open() : null;
            var other = c.Callback == ExplorerCallback.OtherFile ? run.Open(other: true) : null;
            var record = new CallbackRecord();
            var pause = run.Schedule.NewBoundary("A paused in its input callback holding its unit");
            var callback = run.Callback(db, peer, other, record);

            IExplorerUnit unit = null;
            var begin = run.A.Invoke("Begin", () => unit = run.Access.Begin(db));
            run.A.Complete(begin);
            run.Judge(begin, Permit.Success);
            var active = run.A.Invoke("Insert.callback", () => unit.Collection("rows").Insert(ExplorerRun.Input(pause, callback, false)),
                ExplorerSchedule.Extended);
            run.Schedule.Until(() => pause.Reached || active.Done.IsSet, "A reaches its callback", "A/Insert.callback");

            var target = sameCollection ? "rows" : "other";
            var contenders = ExplorerContenders.For(run, db, slot: 2);
            var bCommitted = false;
            var bReachedCommit = false;
            contenders[2] = new Contender
            {
                Op = "Unit.write", Deadline = ExplorerSchedule.Extended,
                Body = () =>
                {
                    using (var second = run.Access.Begin(db2))
                    {
                        second.Collection(target).Upsert(ExplorerModel.Row(7, 70));
                        bReachedCommit = true;
                        bCommitted = second.Commit();
                    }
                },
                Account = work =>
                {
                    Reachability.Sometimes("situation:explorer-units-contend");
                    run.Judge(work, disturbance | Permit.LockTimeout);
                    if (work.Ok && bCommitted) run.Model.Acknowledge(target, 7, 70);
                    else if (!work.Ok && bReachedCommit) run.Model.Uncertain(target, 7, 70);
                }
            };
            var actors = new[] { run.B, run.C, run.D };
            var works = new List<(Contender Contender, ExplorerSchedule.Work Work, ExplorerSchedule.Actor Actor)>();
            for (var i = 0; i < 3; i++)
            {
                var contender = contenders[order[i]];
                var work = actors[i].Invoke(contender.Op, () => contender.Body(), contender.Deadline);
                works.Add((contender, work, actors[i]));
                run.Settle(work, SettleTime);
            }
            pause.Let();
            run.A.Complete(active);
            var commit = active.Ok && unit.Transactional && run.Vector.Seed % 2 == 0;
            var committed = !unit.Transactional && active.Ok;
            if (unit.Transactional)
            {
                var finish = run.A.Invoke(commit ? "Commit" : "Rollback", () =>
                {
                    if (commit) committed = unit.Commit();
                    else unit.Rollback();
                });
                run.A.Complete(finish);
                run.Judge(finish, disturbance);
                if (commit && finish.Ok && !committed && disturbance == Permit.Success)
                    throw new ExplorerFailure("EXPLORER_COMMIT_LOST", "Commit reported no transaction although every operation succeeded");
            }
            foreach (var item in works) item.Actor.Complete(item.Work);
            run.Judge(active, disturbance | Permit.LockTimeout);
            foreach (var item in works) item.Contender.Account(item.Work);
            foreach (var row in new[] { (Id: 2, Value: 20), (Id: 3, Value: 30) })
            {
                if (committed) run.Model.Acknowledge("rows", row.Id, row.Value);
                else if (!unit.Transactional && !active.Ok) run.Model.Uncertain("rows", row.Id, row.Value);
            }
            run.JudgeCallback(record, "A callback", disturbance);
            run.Account(record, committed);
            if (c.Callback == ExplorerCallback.Dispose && record.Ran && record.Error == null) run.Disposed(db, "Dispose (from callback)");
            if (run.IsDisposed(db)) CallbackPauseScenario.LateOperationRefused(run, db);
        }
    }
}
