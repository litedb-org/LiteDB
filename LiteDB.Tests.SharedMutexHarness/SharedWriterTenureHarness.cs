using LiteDB;
using LiteDB.Client.Shared;
using LiteDB.Engine;

internal static class SharedWriterTenureHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode != "one-file-tenure" && mode != "one-file-tenure-request") return false;
        var observed = false;
        if (mode == "one-file-tenure-request") SharedWriterRequests.RequestStage = stage =>
        {
            if (stage == "request-published" && !observed)
            {
                observed = true;
                Console.WriteLine("requested");
                Console.ReadLine();
            }
            if (stage == "request-admitted") Console.WriteLine("waiting-main");
        };
        using var engine = new SharedEngine(new EngineSettings { Filename = filename, SharedOneFile = true });
        SharedWriterRequests.RequestStage = null;
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        Console.WriteLine("ready");
        string command;
        while ((command = Console.ReadLine()) != null)
        {
            var parts = command.Split(' ');
            var id = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            switch (parts[0])
            {
                case "write":
                    db.GetCollection("tenure").Insert(Row(id));
                    Console.WriteLine("wrote " + id);
                    break;
                case "begin":
                    db.BeginTrans();
                    db.GetCollection("tenure").Insert(Row(id));
                    Console.WriteLine("begun");
                    break;
                case "commit": db.Commit(); Console.WriteLine("committed"); break;
                case "rollback": db.Rollback(); Console.WriteLine("rolled-back"); break;
                case "loop":
                    for (var n = 0; n < 24; n++)
                    {
                        db.GetCollection("tenure").Insert(Row(id * 100 + n));
                        Console.WriteLine("wrote " + (id * 100 + n));
                    }
                    Console.WriteLine("loop-done");
                    break;
                case "close": Console.WriteLine("done"); return true;
                default: throw new InvalidOperationException(command);
            }
        }
        return true;
    }
    private static BsonDocument Row(int id) => new BsonDocument
    { ["_id"] = id, ["value"] = id, ["payload"] = new string('t', 256) };
}
