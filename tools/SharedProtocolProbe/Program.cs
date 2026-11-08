using System;
using System.IO;
using LiteDB;
using LiteDB.Engine;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var expectedBits = int.Parse(args[3]);
            var expectedMajor = int.Parse(args[4]);
            if (IntPtr.Size * 8 != expectedBits || Environment.Version.Major != expectedMajor)
                throw new Exception("Wrong runtime/architecture: " + Environment.Version + "/" + IntPtr.Size * 8);
            var settings = new EngineSettings { Filename = args[1] };
            if (args[2] == "legacy") settings.SharedOneFile = false;
            try
            {
                using (var engine = new SharedEngine(settings))
                using (var db = new LiteDatabase(engine, disposeOnClose: false))
                {
                    if (args[0] == "refused") throw new Exception("Mixed open was admitted");
                    var docs = db.GetCollection("docs");
                    docs.Upsert(new BsonDocument { ["_id"] = 1, ["value"] = "accepted" });
                    docs.EnsureIndex("value");
                    if (docs.FindOne(Query.EQ("value", "accepted"))["_id"].AsInt32 != 1) throw new Exception("Lost indexed data");
                    Console.WriteLine("ready");
                    Console.ReadLine();
                }
            }
            catch (IOException error) when (args[0] == "refused")
            {
                if (!error.ToString().Contains("admission")) throw;
            }
            Console.WriteLine("done");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
