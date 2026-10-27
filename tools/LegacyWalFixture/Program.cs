using System;
using System.IO;
using System.IO.Compression;
using LiteDB;

// Capture while open: disposal would checkpoint the WAL-only second row.
// Neither the released data nor WAL bytes are patched after generation.
internal static class Program
{
    private static void Main(string[] args)
    {
        using var zip = ZipFile.Open(args[0], ZipArchiveMode.Create);
        foreach (var password in new[] { null, "wal-secret" })
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            using var db = new LiteDatabase(new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
                { DataStream = data, LogStream = log, Password = password, Collation = Collation.Binary }));
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 7 });
            db.Checkpoint();
            rows.Insert(new BsonDocument { ["_id"] = 2, ["value"] = 8 });
            if (rows.Count() != 2 || log.Length <= (password == null ? 0 : 8192))
                throw new Exception("Expected two rows and a nonempty legacy WAL");
            var name = password == null ? "plain" : "encrypted";
            using (var target = zip.CreateEntry(name + ".db").Open()) data.WriteTo(target);
            using (var target = zip.CreateEntry(name + "-log.db").Open()) log.WriteTo(target);
        }
    }
}
