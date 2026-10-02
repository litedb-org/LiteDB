namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Historical adapter, access kind <c>handle</c>: a unit is an <see cref="ILiteTransaction"/>
    /// from <c>LiteDatabase.BeginTransaction()</c>. Handles are not thread-affine (any thread may
    /// make the next call once the previous one returned), so every generic scenario runs with
    /// units handed between actors. Compiled only where the handle API exists (capability
    /// <c>handle-api</c>); on upstream the kind reports NOT APPLICABLE.
    /// </summary>
    internal sealed class HandleAccess : IExplorerAccess
    {
        public string Name => "handle";
        public string Capability => "handle-api";
        public bool ThreadAffine => false;
        public bool Transactional => true;

        public IExplorerUnit Begin(LiteDatabase connection) => new Unit(connection.BeginTransaction());

        private sealed class Unit : IExplorerUnit
        {
            private readonly ILiteTransaction _tx;

            internal Unit(ILiteTransaction tx) { _tx = tx; }

            public bool Transactional => true;

            public ILiteCollection<BsonDocument> Collection(string name) => _tx.GetCollection(name);

            /// <summary>
            /// A handle whose statement failed is no longer Active and has nothing to commit (its
            /// writes were rolled back when it failed); otherwise Commit decides.
            /// </summary>
            public bool Commit()
            {
                if (_tx.State != LiteTransactionState.Active) return false;
                _tx.Commit();
                return _tx.State == LiteTransactionState.Committed;
            }

            public void Rollback()
            {
                if (_tx.State == LiteTransactionState.Active) _tx.Rollback();
            }

            public void Dispose() => _tx.Dispose();
        }
    }

    /// <summary>Handle-scenario helpers: the documented refusals and the handle state oracle.</summary>
    internal static class HandleChecks
    {
        /// <summary>Judges <paramref name="work"/>: it must have been refused (never a timeout, never success).</summary>
        internal static void Refused(ExplorerRun run, ExplorerSchedule.Work work)
        {
            if (work.Ok)
                throw new ExplorerFailure("EXPLORER_HANDLE_NOT_REFUSED", work + " succeeded although the handle was busy or holds a reader");
            run.Judge(work, Permit.Refusal);
        }

        internal static void Active(ILiteTransaction tx, string after)
        {
            if (tx.State != LiteTransactionState.Active)
                throw new ExplorerFailure("EXPLORER_HANDLE_NOT_ACTIVE", "a refused call left the handle " + tx.State + " (" + after + ")");
        }

        internal static ILiteTransaction Begin(ExplorerSchedule.Actor actor, LiteDatabase db, ExplorerRun run)
        {
            ILiteTransaction tx = null;
            var begin = actor.Invoke("BeginTransaction", () => tx = db.BeginTransaction());
            actor.Complete(begin);
            run.Judge(begin, Permit.Success);
            return run.Keep(tx);
        }

        /// <summary>Handle scenarios apply only to the handle kind (other kinds have their own scenarios).</summary>
        internal static string OnlyHandle(ExplorerConfiguration c, IExplorerAccess access)
        {
            if (access == null || access.Name != "handle") return "handle scenarios need access=handle";
            if (c.Maintenance != ExplorerMaintenance.None || c.Callback != ExplorerCallback.None || c.Process != ExplorerProcess.Single)
                return "handle scenarios explore the handle alphabet only (maintenance, callback and process are none/single)";
            return null;
        }
    }
}
