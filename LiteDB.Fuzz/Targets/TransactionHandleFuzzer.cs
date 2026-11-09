namespace LiteDB.Fuzz.Targets;

// A scalar oracle never learns committed values from the database under test.
// Each turn uses retired threads and a new facade; terminal outcomes and indexes
// are checked through two cold opens, including abandonment by facade disposal.
internal sealed class TransactionHandleFuzzer : IFuzzTarget
{
    public string Name => "transaction-handle";
    public string Description => "Model-checked handle commit, rollback, retired-thread handoff and facade abandonment on Direct/default/legacy Shared.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "handles.db"));
        var model = new SortedDictionary<int, int>();
        var outcomes = new int[3];
        var peerModel = new SortedDictionary<int, int>();
        var mode = (int)((uint)context.Seed % 3);
        var settings = new ConnectionString { Filename = file,
            Connection = mode == 0 ? ConnectionType.Direct : ConnectionType.Shared,
            TransactionPageLimit = 1, Password = (context.Seed & 1) == 0 ? null : "secret" };
        if (mode == 2) settings.SharedOneFile = false;
        using (var setup = new LiteDatabase(settings))
        {
            setup.GetCollection("rows").EnsureIndex("value", true);
            setup.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 9 });
        }
        while (context.Next())
        {
            var id = context.Random.Next(1, 33);
            var value = context.Steps * 100 + id;
            // Guarantee all outcomes in every >=3-step run, then randomize.
            var outcome = context.Steps <= 3 ? context.Steps - 1 : context.Random.Next(3);
            context.Trace("handle-turn", new { mode, id, value, outcome });
            using (var db = new LiteDatabase(settings))
            using (var peer = new LiteDatabase(settings))
            {
                ILiteTransaction other = null;
                if (mode == 0)
                {
                    FuzzThread.Run(() => other = peer.BeginTransaction());
                    FuzzThread.Run(() => other.GetCollection("peer").Upsert(new BsonDocument { ["_id"] = id, ["value"] = value }));
                }
                ILiteTransaction tx = null;
                FuzzThread.Run(() => tx = db.BeginTransaction());
                using (tx)
                {
                    FuzzThread.Run(() => tx.GetCollection("rows").Upsert(new BsonDocument { ["_id"] = id, ["value"] = value }));
                    FuzzThread.Run(() => context.Check(tx.GetCollection("rows").FindById(id)["value"].AsInt32 == value, "Handle lost its pending value."));
                    if (outcome == 0) { FuzzThread.Run(tx.Commit); model[id] = value; }
                    else if (outcome == 1) FuzzThread.Run(tx.Rollback);
                    else db.Dispose();
                    if (other != null)
                    {
                        using (other)
                        {
                            if (outcome == 1) { FuzzThread.Run(other.Commit); peerModel[id] = value; }
                            else FuzzThread.Run(other.Rollback);
                        }
                    }
                    context.Check(tx.State == (outcome == 0 ? LiteTransactionState.Committed : LiteTransactionState.RolledBack), "Wrong terminal state.");
                }
            }
            outcomes[outcome]++;
            for (var reopen = 0; reopen < 2; reopen++)
            {
                using var db = new LiteDatabase(settings);
                var rows = db.GetCollection("rows");
                var actual = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
                context.Check(actual.Length == model.Count, "Partial rollback or lost commit: row count.");
                context.Check(actual.Select(x => (x["_id"].AsInt32, x["value"].AsInt32)).SequenceEqual(model.Select(x => (x.Key, x.Value))), "Full state differs from committed oracle.");
                foreach (var pair in model)
                    context.Check(rows.Find(Query.EQ("value", pair.Value)).Single()["_id"].AsInt32 == pair.Key, "Stale indexed state.");
                context.Check(db.GetCollection("peer").FindAll().OrderBy(x => x["_id"].AsInt32).Select(x => (x["_id"].AsInt32, x["value"].AsInt32)).SequenceEqual(peerModel.Select(x => (x.Key, x.Value))), "Peer handle borrowed another owner or lost its independent outcome.");
                context.Check(db.GetCollection("sentinel").FindById(9) != null, "Unrelated sentinel lost.");
            }
        }
        DatabaseIntegrityVerifier.Verify(context, file, settings.Password);
        context.Metrics["commits"] = outcomes[0];
        context.Metrics["rollbacks"] = outcomes[1];
        context.Metrics["abandonments"] = outcomes[2];
        context.Metrics["mode"] = mode;
        return Task.CompletedTask;
    }
}
