using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedOneFileWarmHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-warm-writer" && mode != "one-file-warm-reader") return false;
        using var engine = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true })
        { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        db.CheckpointSize = 0;
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
            EngineState.SimulateProcessCrash = pause;
            try
            {
                if (command == "commit")
                {
                    db.GetCollection("docs").Update(new BsonDocument
                        { ["_id"] = 1, ["value"] = 1, ["payload"] = new string('x', 1024) });
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
