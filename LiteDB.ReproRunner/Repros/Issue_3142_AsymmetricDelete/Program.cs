using System.IO.Compression;
using LiteDB.Tests;
using LiteDB;
using LiteDB.Vector;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3142_AsymmetricDelete;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(Path.GetTempPath(), "hnswc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var filename = Path.Combine(directory, "vectors.db");
            using (var resource = File.OpenRead(ArtifactFixtures.Path("HnswAsymmetric_6b2ecd3af.zip")))
            using (var archive = new ZipArchive(resource))
            using (var input = archive.GetEntry("vectors.db")!.Open())
            using (var output = File.Create(filename)) input.CopyTo(output);
            using var db = new LiteDatabase(filename);
            var docs = db.GetCollection("docs");
            if (docs.Count() != 73) throw new InvalidOperationException("Fixture document control failed.");
            var reproduced = false;
            try
            {
                if (!docs.Delete(1)) throw new InvalidOperationException("Delete returned false.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("Queue empty")) { reproduced = true; }
            if (docs.Count() != (reproduced ? 73 : 72))
                throw new InvalidOperationException("Deletion atomicity control failed.");
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
}
