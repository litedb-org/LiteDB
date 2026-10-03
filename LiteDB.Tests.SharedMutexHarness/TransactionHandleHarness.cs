using LiteDB;

internal static class TransactionHandleHarness
{
    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("handle-", StringComparison.Ordinal) && mode != "legacy-loop") return false;
        if (mode == "handle-first-culture")
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
        using var db = new LiteDatabase(new ConnectionString { Filename = filename, Password = password,
            Connection = args[4] == "shared" ? ConnectionType.Shared : ConnectionType.Direct,
            TransactionPageLimit = 1 });
        if (mode == "handle-first-culture")
        {
            using var tx = db.BeginTransaction();
            tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "I" });
            tx.GetCollection("rows").EnsureIndex("value");
            tx.Commit();
            if (db.Collation.Culture.Name != "tr-TR") throw new Exception("Caller default collation was lost");
            Console.WriteLine("done");
            return true;
        }
        if (mode == "handle-close-idle" || mode == "handle-close-active")
        {
            if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, 1))
                throw new InvalidOperationException("Could not constrain the isolated worker pool");
            Task.Run(() =>
            {
                if (mode == "handle-close-active")
                {
                    var tx = db.BeginTransaction();
                    tx.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = 84 });
                }
                db.Dispose();
            }).GetAwaiter().GetResult();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "handle-loop" || mode == "legacy-loop")
        {
            // Sequential commits until killed. Each commit spills to the WAL (TransactionPageLimit=1)
            // and is acknowledged on stdout only after Commit returned.
            Console.WriteLine("ready");
            for (var i = 100; ; i++)
            {
                var id = mode == "legacy-loop" ? 100000 + i : i;
                var doc = new BsonDocument { ["_id"] = id, ["value"] = 1000 + id, ["payload"] = new string('y', 3000 + (i % 7) * 4000) };
                var last = new BsonDocument { ["_id"] = 1, ["value"] = 42, ["last"] = id };
                if (mode == "handle-loop")
                {
                    using var tx = db.BeginTransaction();
                    tx.GetCollection("rows").Insert(doc);
                    tx.GetCollection("rows").Update(last);
                    tx.Commit();
                }
                else
                {
#pragma warning disable CS0618
                    db.BeginTrans();
                    db.GetCollection("rows").Insert(doc);
                    db.GetCollection("rows").Update(last);
                    db.Commit();
#pragma warning restore CS0618
                }
                Console.WriteLine("ack " + id);
            }
        }
        if (mode == "handle-writer")
        {
            // Begin waits for the remote owner's native writer mutex; the parent observes
            // that this begin does not complete until the owner releases it.
            Console.WriteLine("begin");
            using var writer = db.BeginTransaction();
            writer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 3, ["value"] = 126 });
            writer.Commit();
            Console.WriteLine("done");
            return true;
        }
        if (mode == "handle-hold-sequential")
        {
            using var first = db.BeginTransaction();
            if (first.GetCollection("rows").FindById(1) == null) throw new Exception("Missing committed seed");
            first.Commit();
        }
        ILiteTransaction? transaction = null;
        Exception? error = null;
        var creator = new Thread(() =>
        {
            try
            {
                transaction = db.BeginTransaction();
                transaction.GetCollection("rows").Insert(new BsonDocument
                { ["_id"] = 2, ["value"] = 84, ["payload"] = new string('x', 50000) });
            }
            catch (Exception failure) { error = failure; }
        });
        creator.Start();
        creator.Join();
        if (error != null) throw error;
        Console.WriteLine("ready");
        var command = Console.ReadLine();
        var completer = new Thread(() =>
        {
            try
            {
                if (command == "commit") transaction!.Commit();
                else transaction!.Rollback();
            }
            catch (Exception failure) { error = failure; }
        });
        completer.Start();
        completer.Join();
        if (error != null) throw error;
        Console.WriteLine("done");
        // Keep the completed handle and facade alive while a peer acquires writer ownership.
        Console.ReadLine();
        transaction!.Dispose();
        return true;
    }
}
