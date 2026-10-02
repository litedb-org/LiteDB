using System;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Handle alphabet (fork PR #133 explorer, schedules 12-23, generalized): two handles on one
    /// Direct connection (Shared admits one live handle per file and process) update, insert and delete in their
    /// own collections, then each pauses inside a bound input callback; meanwhile C reads both
    /// collections through ordinary calls and must see no uncommitted value. The handles finish in
    /// either order, one possibly rolling back; acknowledged effects survive the cold check exactly.
    /// </summary>
    internal sealed class HandleIndependenceScenario : IExplorerScenario
    {
        public string Name => "handle-independence";
        public string Description => "Two independent handles with pending writes; ordinary readers see no uncommitted state.";
        public int Variants => 2 * 3;

        public string NotApplicable(ExplorerConfiguration configuration, IExplorerAccess access) =>
            HandleChecks.OnlyHandle(configuration, access) ??
            (configuration.Shared ? "handle-independence needs two live handles on one file; Shared admits one per file and process" : null);

        public void Run(ExplorerRun run, int variant)
        {
            var reverse = variant % 2 == 1;
            var rollback = variant / 2; // 0: both commit, 1: A rolls back, 2: B rolls back
            var db = run.Open();
            // One Direct connection: a second Direct connection to the file is outside the contract (refused by admission).
            var peer = db;
            var first = HandleChecks.Begin(run.A, db, run);
            var second = HandleChecks.Begin(run.B, peer, run);
            Action<ILiteTransaction, string> change = (tx, name) =>
            {
                var rows = tx.GetCollection(name);
                ExplorerModel.Require(rows.Update(ExplorerModel.Row(1, 30)), "handle-update-missed", "update missed the existing row");
                rows.Insert(ExplorerModel.Row(3, 40));
                ExplorerModel.Require(rows.Delete(3), "handle-delete-missed", "delete missed the pending row");
            };
            run.A.Run("Change", () => change(first, "rows"));
            run.B.Run("Change", () => change(second, "other"));
            var firstInside = run.Schedule.NewBoundary("A pending writes");
            var secondInside = run.Schedule.NewBoundary("B pending writes");
            var aw = run.A.Invoke("Insert.callback", () => first.GetCollection("rows").Insert(ExplorerRun.Input(firstInside, () => { }, false)),
                ExplorerSchedule.Extended);
            firstInside.Wait();
            var bw = run.B.Invoke("Insert.callback", () => second.GetCollection("other").Insert(ExplorerRun.Input(secondInside, () => { }, false)),
                ExplorerSchedule.Extended);
            secondInside.Wait();
            var read = run.C.Invoke("Read.uncommitted", () =>
            {
                foreach (var name in new[] { "rows", "other" })
                {
                    var rows = db.GetCollection(name);
                    ExplorerModel.Require(rows.FindById(1)["value"] == 10, "handle-dirty-update", name + ": an uncommitted update was visible");
                    ExplorerModel.Require(rows.FindById(2) == null && rows.FindById(3) == null, "handle-dirty-insert",
                        name + ": an uncommitted insert was visible");
                }
            });
            run.C.Complete(read);
            run.Judge(read, Permit.Success);
            Action finishA = () => Finish(run, run.A, firstInside, aw, first, "rows", rollback != 1);
            Action finishB = () => Finish(run, run.B, secondInside, bw, second, "other", rollback != 2);
            if (reverse) { finishB(); finishA(); }
            else { finishA(); finishB(); }
        }

        private static void Finish(ExplorerRun run, ExplorerSchedule.Actor actor, ExplorerSchedule.Boundary inside,
            ExplorerSchedule.Work insert, ILiteTransaction tx, string collection, bool commit)
        {
            inside.Let();
            actor.Complete(insert);
            run.Judge(insert, Permit.Success);
            var finish = actor.Invoke(commit ? "Commit" : "Rollback", commit ? (Action)tx.Commit : tx.Rollback);
            actor.Complete(finish);
            run.Judge(finish, Permit.Success);
            if (!commit) return;
            run.Model.Acknowledge(collection, 1, 30);
            run.Model.Acknowledge(collection, 2, 20);
            run.Model.Acknowledge(collection, 3, 30);
        }
    }
}
