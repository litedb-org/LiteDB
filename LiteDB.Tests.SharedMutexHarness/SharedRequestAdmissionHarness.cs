using LiteDB;
using LiteDB.Client.Shared;

internal static class SharedRequestAdmissionHarness
{
    internal static bool TryRun(string mode, string filename, string[] args)
    {
        if (mode == "request-dms-hold")
        {
            using var locks = new SharedRangeLocks(filename);
            if (!locks.TryExclusiveDms()) throw new Exception("DMS was already owned");
            Console.WriteLine("ready");
            Console.ReadLine();
            return true;
        }
        if (mode != "request-open-loop") return false;
        var worker = int.Parse(args[4]);
        Console.WriteLine("ready");
        Console.ReadLine();
        for (var iteration = 0; iteration < 40; iteration++)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared });
            var docs = db.GetCollection("requests");
            var id = worker * 1000 + iteration;
            docs.Insert(new BsonDocument { ["_id"] = id, ["value"] = "worker:" + worker });
            if (docs.FindById(id)["value"].AsString != "worker:" + worker) throw new Exception("Incorrect request read");
        }
        Console.WriteLine("done");
        return true;
    }
}
