using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOneFileWarmHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-warm-writer" && mode != "one-file-warm-reader" && mode != "one-file-warm-generations" && mode != "one-file-tenure-close-cut") return false;
        using var engine = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] })
        { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        db.CheckpointSize = 0;
        if (mode == "one-file-tenure-close-cut")
        {
            BsonDocument Row(int value) => new BsonDocument
                { ["_id"] = 1, ["value"] = value, ["payload"] = new string('x', 1024) };
            db.GetCollection("docs").Update(Row(1));
            if (!db.BeginTrans()) throw new Exception("Missing explicit transaction");
            db.GetCollection("docs").Update(Row(99));
            Console.WriteLine("ready");
            Console.ReadLine();
            Action<string> pause = stage =>
            {
                if (stage != args[4]) return;
                Console.WriteLine("cut");
                Console.ReadLine();
            };
            engine.OwnershipStage = pause;
            SharedMemoryReaderTable.PublicationStage = pause;
            SharedOwnershipEvents.Releasing = _ => pause("main-release");
            engine.Dispose();
            return true;
        }
        if (mode == "one-file-warm-generations")
        {
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1);
            var hits = engine.CoordinatedReadHits;
            using var oldest = engine.Query("docs", new Query());
            if (engine.CoordinatedReadHits != hits + 1) throw new Exception("Oldest was not warm");
            var oldestVersion = engine.MappedReaderTable.ReadCache.Snapshot.Status.Version;
            using var peer = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true, Password = args[3] == "-" ? null : args[3] });
            using var peerDb = new LiteDatabase(peer, disposeOnClose: false);
            peerDb.CheckpointSize = 0;
            var value = args.Length > 4 ? int.Parse(args[4]) : 1;
            Task.Run(() => peerDb.GetCollection("docs").Update(new BsonDocument
                { ["_id"] = 1, ["value"] = value, ["payload"] = new string('x', 1024) })).GetAwaiter().GetResult();
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1);
            hits = engine.CoordinatedReadHits;
            using var current = engine.Query("docs", new Query());
            if (engine.CoordinatedReadHits != hits + 1) throw new Exception("Current was not warm");
            Console.WriteLine("ready");
            Console.WriteLine(engine.MappedReaderTable.Authority.Slot + ":" + engine.MappedReaderTable.Authority.Incarnation);
            Console.WriteLine(oldestVersion + "," + engine.MappedReaderTable.ReadCache.Snapshot.Status.Version);
            Console.ReadLine();
            return true;
        }
        if (mode == "one-file-warm-reader")
        {
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1);
            var hits = engine.CoordinatedReadHits;
            using var reader = engine.Query("docs", new Query());
            if (engine.CoordinatedReadHits <= hits || !reader.Read()) throw new Exception("Reader did not admit warm");
            Console.WriteLine("ready");
            Console.ReadLine();
            return true;
        }
        Console.WriteLine("ready");
        string command;
        while ((command = Console.ReadLine()) != null && command != "exit")
        {
            Action<string> pause = stage =>
            {
                if (args.Length <= 4 || stage != args[4]) return;
                Console.WriteLine("cut");
                Console.ReadLine();
            };
            SharedMemoryReaderTable.PublicationStage = pause;
            EngineState.SimulateProcessCrash = stage =>
            {
                if (args.Length > 4 && args[4] == "writer-uncertain" && stage == "wal-before-durable-flush")
                    throw new IOException("injected failed sync before uncertain publication");
                pause(stage);
            };
            try
            {
                if (command == "commit" || command == "commit2")
                {
                    // A retained close can run after an automatic call returns.
                    // Keep a known pin through this explicit commit, then demand
                    // the actual close before announcing completion at that cut.
                    var closeCut = args.Length > 4 && args[4] == "writer-end";
                    if (closeCut && !db.BeginTrans()) throw new Exception("Missing close-cut transaction");
                    var pin = engine.Pin;
                    db.GetCollection("docs").Update(new BsonDocument
                        { ["_id"] = 1, ["value"] = command == "commit2" ? 2 : 1, ["payload"] = new string('x', 1024) });
                    if (closeCut)
                    {
                        if (!db.Commit()) throw new Exception("Missing close-cut commit");
                        pin!.RequestRelease(false); pin.WaitReleased();
                    }
                    Console.WriteLine("committed");
                }
                else if (command == "metadata")
                {
                    db.RenameCollection("docs", "archive");
                    db.GetCollection("archive").DropIndex("value");
                    db.GetCollection("archive").EnsureIndex("value", "$.value + 10");
                    db.GetCollection("scratch").Insert(new BsonDocument { ["_id"] = 1 });
                    db.DropCollection("scratch");
                    db.GetCollection("docs").Insert(Enumerable.Range(1, 3).Select(id => new BsonDocument
                        { ["_id"] = id, ["value"] = 3, ["payload"] = new string('x', 1024) }));
                    db.GetCollection("docs").EnsureIndex("value");
                    Console.WriteLine("metadata-done");
                }
                else if (command.StartsWith("reset ", StringComparison.Ordinal))
                {
                    var value = int.Parse(command.Substring(6));
                    db.GetCollection("docs").Update(new BsonDocument
                        { ["_id"] = 1, ["value"] = value, ["payload"] = new string('x', 1024) });
                    db.Checkpoint();
                    if (new FileInfo(FileHelper.GetLogFile(filename)).Length != (args[3] == "-" ? 0 : Constants.PAGE_SIZE))
                        throw new Exception("Full WAL reset did not occur");
                    Console.WriteLine("reset-done");
                }
                else if (command == "checkpoint") { db.Checkpoint(); Console.WriteLine("checkpointed"); }
                else throw new Exception("Unknown warm command: " + command);
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; EngineState.SimulateProcessCrash = null; }
        }
        return true;
    }
}
