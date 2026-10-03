using System;
using System.Collections.Generic;

namespace LiteDB.ConcurrencyTesting
{
    internal sealed partial class TransactionInterleavingExplorer
    {
        private static readonly int[][] Permutations = { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 },
            new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };

        /// <summary>Overlap refusals while a call executes, then a bound reader handed across threads.</summary>
        private void OverlapAndReaderTransfer(int order, bool usePeer, int seed)
        {
            OpenPeers(usePeer, out var db, out var peer);
            _otherModel = new ExplorerDatabase(_model.Connection.Filename + ".other", _shared, _model.Connection.Password != null);
            var otherDatabase = Keep(_otherModel.Open());
            ILiteTransaction tx = null;
            _a.Run("begin", () => tx = db.BeginTransaction());
            Keep(tx);
            var rows = tx.GetCollection("rows");
            var inside = _schedule.NewBoundary("A inside bound input callback");
            var writing = _a.Invoke("insert-callback", () => rows.Insert(Input(inside, 2, 20, () =>
            {
                // An ordinary call never silently enlists in the handle. Shared must
                // refuse its native dependency, while Direct unrelated writes are legal.
                Action ordinary = () => peer.GetCollection("other").Insert(ExplorerDatabase.Row(2, 50));
                if (_shared) ExplorerDatabase.Refused(ordinary, ExplorerDatabase.SharedCallbackRefusal);
                else { ordinary(); _model.Acknowledge("other", 2, 50); }
                otherDatabase.GetCollection("rows").Insert(ExplorerDatabase.Row(2, 60));
                _otherModel.Acknowledge("rows", 2, 60);
                _schedule.Event("callback same-file policy and other-database positive control reached");
            })));
            inside.Wait();
            // Every permutation explores a different order of three enabled contenders while
            // A is verifiably executing the same handle. Timeouts are never accepted refusals.
            var actions = new Action[]
            {
                () => ExplorerDatabase.Refused(() => rows.FindById(1)),
                () => ExplorerDatabase.Refused(tx.Commit),
                () => ExplorerDatabase.Refused(tx.Rollback)
            };
            foreach (var operation in Permutations[order])
                _b.Run("overlap-" + operation, () =>
                {
                    actions[operation]();
                    ExplorerDatabase.Require(tx.State == LiteTransactionState.Active, "individual overlap refusal changed transaction outcome");
                });
            ExplorerDatabase.Require(tx.State == LiteTransactionState.Active, "overlap aborted legitimate writer");
            inside.Release(); _a.Complete(writing);

            IEnumerator<BsonDocument> cursor = null;
            _a.Run("open-bound-cursor", () =>
            {
                cursor = rows.FindAll().GetEnumerator();
                ExplorerDatabase.Require(cursor.MoveNext(), "bound cursor empty");
            });
            Keep(cursor);
            _b.Run("commit-with-reader", () =>
            {
                ExplorerDatabase.Refused(tx.Commit, ExplorerDatabase.ReaderRefusal);
                ExplorerDatabase.Require(tx.State == LiteTransactionState.Active, "reader refusal changed transaction outcome");
            });
            _c.Run("transfer-read-dispose", () =>
            {
                var ids = new List<int> { cursor.Current["_id"].AsInt32 };
                while (cursor.MoveNext()) ids.Add(cursor.Current["_id"].AsInt32);
                ids.Sort();
                ExplorerDatabase.Require(string.Join(",", ids) == "1,2", "bound reader transaction contents");
                cursor.Dispose();
            });
            if ((seed & 1) == 0)
            {
                _b.Run("commit-transferred", tx.Commit);
                _model.Acknowledge("rows", 2, 20);
            }
            else _b.Run("rollback-transferred", tx.Rollback);
            _c.Run("ordinary-read-after-owner-release", () => ExplorerDatabase.Require(
                peer.GetCollection("sentinel").FindById(42)["value"] == 900, "independent read"));
            _c.Run("disposed-reader-refusal", () =>
            {
                try { cursor.MoveNext(); }
                catch (ObjectDisposedException) { return; }
                throw new InvalidOperationException("Disposed reader accepted use");
            });
        }

        /// <summary>
        /// Direct: two handles on one engine with pending updates, deletes and an executing insert
        /// at once. <paramref name="variant"/>: completion order (bit 0) and which handle rolls
        /// back (none, A, B). <paramref name="seed"/> picks the facade that checks isolation.
        /// </summary>
        private void IndependentHandles(int variant, bool usePeer, int seed)
        {
            OpenPeers(usePeer, out var db, out var peer);
            ILiteTransaction first = null, second = null;
            _a.Run("begin-independent-A", () => first = db.BeginTransaction());
            _b.Run("begin-independent-B", () => second = peer.BeginTransaction());
            Keep(first); Keep(second);
            var firstInside = _schedule.NewBoundary("A pending writes");
            var secondInside = _schedule.NewBoundary("B pending writes");
            Action<ILiteTransaction, string> change = (tx, name) =>
            {
                var rows = tx.GetCollection(name);
                ExplorerDatabase.Require(rows.Update(ExplorerDatabase.Row(1, 30)), "update missed existing row");
                rows.Insert(ExplorerDatabase.Row(3, 40));
                ExplorerDatabase.Require(rows.Delete(3), "delete missed pending row");
            };
            _a.Run("update-delete-A", () => change(first, "rows"));
            _b.Run("update-delete-B", () => change(second, "other"));
            var aw = _a.Invoke("insert-A", () => first.GetCollection("rows").Insert(Input(firstInside, 2, 20)));
            firstInside.Wait();
            var bw = _b.Invoke("insert-B", () => second.GetCollection("other").Insert(Input(secondInside, 2, 20)));
            secondInside.Wait();
            var observer = (seed & 1) == 0 ? db : peer;
            _c.Run("no-uncommitted-visibility", () =>
            {
                foreach (var name in new[] { "rows", "other" })
                {
                    var rows = observer.GetCollection(name);
                    ExplorerDatabase.Require(rows.FindById(1)["value"] == 10, "dirty updated value");
                    ExplorerDatabase.Require(rows.FindById(2) == null && rows.FindById(3) == null, "dirty insert");
                }
            });
            var reverse = variant % 2 != 0;
            var rollback = variant / 2;
            Action finishA = () =>
            {
                firstInside.Release(); _a.Complete(aw);
                _a.Run("finish-A", rollback == 1 ? (Action)first.Rollback : first.Commit);
                if (rollback != 1) { _model.Acknowledge("rows", 1, 30); _model.Acknowledge("rows", 2, 20); }
            };
            Action finishB = () =>
            {
                secondInside.Release(); _b.Complete(bw);
                _b.Run("finish-B", rollback == 2 ? (Action)second.Rollback : second.Commit);
                if (rollback != 2) { _model.Acknowledge("other", 1, 30); _model.Acknowledge("other", 2, 20); }
            };
            if (reverse) { finishB(); finishA(); } else { finishA(); finishB(); }
        }
    }
}
