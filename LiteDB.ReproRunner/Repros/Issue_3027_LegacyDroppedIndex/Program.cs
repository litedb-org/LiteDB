using System.IO.Compression;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_LegacyDroppedIndex;

/// <summary>
/// Regression since 5.0.21 fixed by PR #3027 (guard: LegacyDroppedIndex_Tests). 5.0.21 never cleared
/// the bytes after a collection page's index list, so a DropIndex leaves the tail of the dropped index
/// entries there. Since vector indexes (#2678) the engine read a vector section right after the index
/// list without checking for one, took those stale bytes for vector metadata, and every insert into
/// the collection failed with "request page must be less or equals lastest page in data file", after
/// the writable open had already converted the file, so 5.0.21 could not open it any more either.
///
/// customers.db of DropIndex_5_0_21.zip (LiteDB-Artifacts) was written by the LiteDB 5.0.21
/// package: collection "customers" with indexes Name, Age and CustomerId, 200 documents, then
/// DropIndex("Age"). 5.0.21 inserts, updates, deletes and re-creates the index in it.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const string Defect = "request page must be less or equals lastest page in data file";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_LegacyDroppedIndex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host, Path.Combine(directory, "customers.db"));
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host, string path)
    {
        File.WriteAllBytes(path, Fixture("DropIndex_5_0_21.zip", "customers.db"));
        Require(File.ReadAllBytes(path)[59] == 8, "customers.db is not a 5.0.21 (file version 8) file");

        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("customers");
            Require(col.Count() == 200, $"customers.db holds {col.Count()} documents, expected 200");
            try
            {
                col.Insert(new BsonDocument { ["_id"] = 1000, ["Name"] = "x", ["Age"] = 1, ["CustomerId"] = "C1000" });
            }
            catch (LiteException error) when (error.Message.Contains(Defect))
            {
                host.SendLog($"Insert into the collection 5.0.21 dropped an index from threw {error.GetType().Name} {error.ErrorCode}: {error.Message}");
                return (Reproduced, $"REPRODUCED: the collection 5.0.21 dropped index Age from is not writable: {Defect}");
            }
            Require(col.Update(new BsonDocument { ["_id"] = 1, ["Name"] = "y", ["Age"] = 2, ["CustomerId"] = "C1" }), "the update of _id 1 found no document");
            Require(col.Delete(2), "the delete of _id 2 found no document");
            col.EnsureIndex("Age");
            Require(col.Count(Query.EQ("CustomerId", "C5")) == 1, "CustomerId = C5 does not find one document");
        }

        using (var db = new LiteDatabase(path))
        {
            var col = db.GetCollection("customers");
            var count = col.Count();
            var age2 = col.Count(Query.EQ("Age", 2)); // 92, 182 and the updated 1
            Require(count == 200 && age2 == 3 && col.FindById(1000)?["CustomerId"] == "C1000",
                $"reopened: {count} documents, {age2} with Age 2 (expected 200 and 3), inserted document {col.FindById(1000)}");
        }

        return (Fixed, "FIXED: the collection 5.0.21 dropped index Age from accepts insert, update, delete and EnsureIndex, and reopens with every document");
    }

    private static byte[] Fixture(string archive, string entry)
    {
        // LiteDB-Artifacts compatibility/fixtures, hash-verified ($LITEDB_ARTIFACTS_DIR, cache or download).
        using var zip = ZipFile.OpenRead(LiteDB.Tests.ArtifactFixtures.Path(archive));
        using var source = (zip.GetEntry(entry) ?? throw new InvalidOperationException($"{archive} has no {entry}")).Open();
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
