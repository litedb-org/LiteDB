using System.IO.Compression;
using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_LegacyDamagedDocument;

/// <summary>
/// Regression since 5.0.21 fixed by PR #3027 (guard: LegacyDamagedDocument_Tests). 5.0.21 opens a file
/// with one damaged document, reads the others, and its rebuild keeps all three. A writable open of
/// the current format must first migrate every index from the documents, which the damage prevents.
/// The failed migration surfaced only an internal ENSURE message, and even with Auto-Rebuild=true the
/// first open failed: the file could be repaired only by a later open. Fixed: the first Auto-Rebuild
/// open rebuilds the file and keeps documents 1 and 3 and the readable part of document 2.
///
/// damaged.db of LiteDB.Tests/Resources/DamagedDocument_5_0_21.zip was written by the LiteDB 5.0.21
/// package: collection "c" with {_id: 1..3, a: "keep-i", b: "tail-i-zzz..."}, then the BSON length of
/// the string "b" of document 2 was overwritten with 0x7FFFFFF0.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_LegacyDamagedDocument-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host, Path.Combine(directory, "damaged.db"));
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
        File.WriteAllBytes(path, Fixture("DamagedDocument_5_0_21.zip", "damaged.db"));
        Require(File.ReadAllBytes(path)[59] == 8, "damaged.db is not a 5.0.21 (file version 8) file");
        var autoRebuild = $"Filename={path};Auto-Rebuild=true";

        LiteDatabase opened;
        try
        {
            opened = new LiteDatabase(autoRebuild);
        }
        catch (LiteException error)
        {
            host.SendLog($"First Auto-Rebuild open threw LiteException {error.ErrorCode}: {error.Message}");
            string second;
            try
            {
                using var db = new LiteDatabase(autoRebuild);
                second = "opened: " + (Salvaged(db) ?? "documents 1, 2 (its readable part) and 3 kept");
            }
            catch (Exception again)
            {
                second = $"threw {again.GetType().Name}: {again.Message}";
            }
            host.SendLog($"Second Auto-Rebuild open {second}");
            return (Reproduced, "REPRODUCED: the first Auto-Rebuild open of a 5.0.21 file with one damaged document failed with " +
                $"LiteException {error.ErrorCode}: {error.Message}");
        }

        string? first;
        using (opened) first = Salvaged(opened);
        Require(first == null, $"the first Auto-Rebuild open did not salvage the file: {first}");
        using (var reopened = new LiteDatabase(path))
        {
            var count = reopened.GetCollection("c").Count();
            Require(count == 3, $"a plain reopen after the rebuild found {count} documents, expected 3");
        }
        return (Fixed, "FIXED: the first Auto-Rebuild open rebuilt the 5.0.21 file and kept documents 1 and 3 and the readable part of document 2");
    }

    /// <summary>Null when documents 1 and 3 are whole and document 2 kept its readable part {_id, a}; else what differs.</summary>
    private static string? Salvaged(LiteDatabase db)
    {
        var col = db.GetCollection("c");
        var ids = col.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
        if (!ids.SequenceEqual(new[] { 1, 2, 3 })) return $"kept _id [{string.Join(",", ids)}], expected 1, 2, 3";
        foreach (var id in new[] { 1, 3 })
        {
            var doc = col.FindById(id);
            if (doc["a"] != "keep-" + id || !doc["b"].AsString.StartsWith($"tail-{id}-", StringComparison.Ordinal)) return $"document {id} is {doc}";
        }
        var partial = col.FindById(2);
        if (!partial.Keys.OrderBy(x => x).SequenceEqual(new[] { "_id", "a" }) || partial["a"] != "keep-2")
            return $"document 2 is {partial}, expected its readable part {{_id: 2, a: keep-2}}";
        return null;
    }

    private static byte[] Fixture(string archive, string entry)
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream(archive)
            ?? throw new InvalidOperationException($"{archive} is not embedded");
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
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
