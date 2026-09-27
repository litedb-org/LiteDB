using System;
using System.IO;
using System.Linq;
using LiteDB;

internal static class DamagedFile
{
    internal static void Verify(string filename)
    {
        var before = File.ReadAllBytes(filename);
        foreach (var readOnly in new[] { true, false })
        {
            try
            {
                using var db = new LiteDatabase(new ConnectionString { Filename = filename, ReadOnly = readOnly, LegacyIndexScan = true });
                if (!readOnly) throw new Exception("Damaged source unexpectedly opened for writing");
            }
            catch (LiteException ex) when (ex.ErrorCode == LiteException.INVALID_DATAFILE_STATE)
            {
                Console.WriteLine("Expected corruption: " + ex.Message);
            }
            if (!File.ReadAllBytes(filename).SequenceEqual(before)) throw new Exception("Inspection mutated damaged source");
        }
        int recovered;
        int errors;
        using (var db = new LiteDatabase(new ConnectionString { Filename = filename, AutoRebuild = true }))
        {
            recovered = db.GetCollection("directories").FindAll().Count();
            errors = db.GetCollection("_rebuild_errors").Count();
            if (recovered != 6824 || errors != 6) throw new Exception("Salvage changed the known recoverable corpus or its error report");
            db.GetCollection("salvage_probe").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "persisted" });
        }
        var backup = Path.Combine(Path.GetDirectoryName(filename), Path.GetFileNameWithoutExtension(filename) + "-backup" + Path.GetExtension(filename));
        if (!File.ReadAllBytes(backup).SequenceEqual(before)) throw new Exception("Salvage changed the backup evidence");
        using (var db = new LiteDatabase(filename))
        {
            if (db.GetCollection("directories").FindAll().Count() != recovered || db.GetCollection("_rebuild_errors").Count() != errors ||
                db.GetCollection("salvage_probe").FindById(1)["value"].AsString != "persisted") throw new Exception("Salvage reopen lost data");
        }
        Console.WriteLine("PASS issue #3022 real damaged file: " + recovered + " recovered documents, " + errors + " reported errors");
    }
}
