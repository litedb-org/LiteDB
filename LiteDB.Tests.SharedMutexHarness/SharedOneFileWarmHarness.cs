using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOneFileWarmHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-warm-writer" && mode != "one-file-warm-reader" && mode != "one-file-warm-generations") return false;
        using var engine = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true })
        { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        db.CheckpointSize = 0;
        if (mode == "one-file-warm-generations")
        {
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1);
            var hits = engine.CoordinatedReadHits;
            using var oldest = engine.Query("docs", new Query());
            if (engine.CoordinatedReadHits != hits + 1) throw new Exception("Oldest was not warm");
            var oldestVersion = engine.MappedReaderTable.ReadCache.Snapshot.Status.Version;
            using var peer = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true });
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
                    db.GetCollection("docs").Update(new BsonDocument
                        { ["_id"] = 1, ["value"] = command == "commit2" ? 2 : 1, ["payload"] = new string('x', 1024) });
                    Console.WriteLine("committed");
                }
                else if (command == "checkpoint") { db.Checkpoint(); Console.WriteLine("checkpointed"); }
                else throw new Exception("Unknown warm command: " + command);
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; EngineState.SimulateProcessCrash = null; }
        }
        return true;
    }
}
