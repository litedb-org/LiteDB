using System.IO.Compression;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3141_DisconnectedDrop;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(Path.GetTempPath(), "hnswd-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "vectors.db");
            using (var resource = typeof(Program).Assembly.GetManifestResourceStream("disconnected.zip"))
            using (var zip = new ZipArchive(resource!)) zip.GetEntry("vectors.db")!.ExtractToFile(file);
            Dictionary<int, string> expected;
            using (var db = new LiteDatabase(file))
            {
                var docs = db.GetCollection("computed");
                expected = docs.FindAll().ToDictionary(x => x["_id"].AsInt32, x => x.ToString());
                if (expected.Count != 20 || Pages(db) == 0) throw new InvalidOperationException("Fixture control failed.");
                if (!docs.DropIndex("coalesced")) throw new InvalidOperationException("Drop failed.");
                db.Checkpoint();
            }
            bool reproduced;
            using (var db = new LiteDatabase(file))
            {
                var actual = db.GetCollection("computed").FindAll().ToArray();
                if (actual.Length != 20 || actual.Any(x => !expected.TryGetValue(x["_id"].AsInt32, out var value) || value != x.ToString()))
                    throw new InvalidOperationException("Document preservation control failed.");
                if (db.Execute("SELECT $ FROM $indexes WHERE $.collection = 'computed' AND $.name = 'coalesced'").ToArray().Length != 0)
                    throw new InvalidOperationException("Index metadata was not dropped.");
                reproduced = Pages(db) != 0;
            }
            var message = reproduced ? "BUG_VERIFIED" : "FIXED_VERIFIED";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Unexpected harness or database failure", new { Exception = error.ToString() });
            return 1;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static int Pages(LiteDatabase db) => db.Execute(
        "SELECT $ FROM $dump WHERE $.collection = 'computed' AND $.pageType = 'VectorIndex'").ToArray().Length;
}
