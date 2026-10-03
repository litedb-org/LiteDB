using System;
using LiteDB.Utils;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Crossed collection locks (the JKamsker/LiteDB#133 explorer's C04 schedule, on legacy transactions):
    /// A's unit holds rows, B's unit holds other, then each writes the other's collection. The
    /// documented resolution is a bounded wait: at least one loser gets LOCK_TIMEOUT, a failed
    /// operation rolls its unit back, and every winner commits. Direct mode with transactional
    /// access only (Shared serializes writers per connection; auto-commit units cannot cross).
    /// The wait-for graph reports this cycle as bounded (allowed when outcomes are correct).
    /// </summary>
    internal sealed class CollectionCycleScenario : IExplorerScenario
    {
        public string Name => "collection-cycle";
        public string Description => "Opposite collection locks held by two units; one loser times out and rolls back.";
        public int Variants => 2;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access)
        {
            if (configuration.Mode != ExplorerMode.Direct) return "collection-cycle needs mode=direct (Shared serializes writers)";
            if (access == null || !access.Transactional) return "collection-cycle needs a transactional access kind (units must hold locks across operations)";
            if (configuration.Maintenance != ExplorerMaintenance.None || configuration.Callback != ExplorerCallback.None ||
                configuration.Process != ExplorerProcess.Single)
                return "collection-cycle explores lock order only (maintenance, callback and process are none/single)";
            return null;
        }

        public void Run(ExplorerRun run, int variant)
        {
            var db = run.Open();
            IExplorerUnit first = null, second = null;
            run.A.Run("Begin", () => first = run.Access.Begin(db));
            run.B.Run("Begin", () => second = run.Access.Begin(db));
            var holdA = run.A.Invoke("Write.hold", () => first.Collection("rows").Upsert(ExplorerModel.Row(2, 20)));
            run.A.Complete(holdA);
            run.Judge(holdA, Permit.Success);
            var holdB = run.B.Invoke("Write.hold", () => second.Collection("other").Upsert(ExplorerModel.Row(4, 40)));
            run.B.Complete(holdB);
            run.Judge(holdB, Permit.Success);
            ExplorerSchedule.Work aw, bw;
            if (variant == 1)
            {
                bw = run.B.Invoke("Write.cross", () => second.Collection("rows").Upsert(ExplorerModel.Row(5, 30)), ExplorerSchedule.Extended);
                aw = run.A.Invoke("Write.cross", () => first.Collection("other").Upsert(ExplorerModel.Row(3, 30)), ExplorerSchedule.Extended);
            }
            else
            {
                aw = run.A.Invoke("Write.cross", () => first.Collection("other").Upsert(ExplorerModel.Row(3, 30)), ExplorerSchedule.Extended);
                bw = run.B.Invoke("Write.cross", () => second.Collection("rows").Upsert(ExplorerModel.Row(5, 30)), ExplorerSchedule.Extended);
            }
            run.A.Complete(aw);
            run.B.Complete(bw);
            run.Judge(aw, Permit.LockTimeout);
            run.Judge(bw, Permit.LockTimeout);
            ExplorerModel.Require(!aw.Ok || !bw.Ok, "cycle-has-loser", "both crossed writers succeeded although each waited for the other");
            Reachability.Sometimes("situation:explorer-crossed-locks-timed-out-loser");
            Finish(run, run.A, first, aw.Ok, ("rows", 2, 20), ("other", 3, 30));
            Finish(run, run.B, second, bw.Ok, ("other", 4, 40), ("rows", 5, 30));
        }

        private static void Finish(ExplorerRun run, ExplorerSchedule.Actor actor, IExplorerUnit unit, bool won,
            params (string Collection, int Id, int Value)[] rows)
        {
            var committed = false;
            var finish = actor.Invoke(won ? "Commit" : "Rollback", () =>
            {
                if (won) committed = unit.Commit();
                else unit.Rollback();
            });
            actor.Complete(finish);
            run.Judge(finish, Permit.Success);
            ExplorerModel.Require(!won || committed, "winner-commits", "the winning unit had no transaction to commit");
            if (committed) foreach (var row in rows) run.Model.Acknowledge(row.Collection, row.Id, row.Value);
        }
    }
}
