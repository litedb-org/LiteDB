using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Engine;

internal static class MaintenanceBenchmarks
{
    internal static void Run()
    {
        foreach (var encrypted in new[] { false, true })
        {
            var checkpoint = new double[5];
            var conversion = new double[5];
            long bytes = 0;
            for (var run = -1; run < checkpoint.Length; run++)
            {
                var file = Path.Combine(Path.GetTempPath(), "litedb-maintenance-" + Guid.NewGuid() + ".db");
                var password = encrypted ? "secret" : null;
                var connection = new ConnectionString { Filename = file, Password = password };
                try
                {
                    using (var db = new LiteDatabase(connection))
                    {
                        db.CheckpointSize = 0;
                        db.GetCollection("docs").InsertBulk(Enumerable.Range(0, 10000).Select(id =>
                            new BsonDocument { ["_id"] = id, ["value"] = new string('x', 256) }));
                        var watch = Stopwatch.StartNew();
                        db.Checkpoint();
                        watch.Stop();
                        if (run >= 0) checkpoint[run] = watch.Elapsed.TotalMilliseconds;
                    }
                    bytes = new FileInfo(file).Length;
                    MakeLegacyHeader(file, password);
                    var open = Stopwatch.StartNew();
                    using (var db = new LiteDatabase(connection))
                    {
                        open.Stop();
                        if (run >= 0) conversion[run] = open.Elapsed.TotalMilliseconds;
                        if (db.GetCollection("docs").Count() != 10000) throw new Exception("Conversion lost documents");
                    }
                }
                finally
                {
                    File.Delete(file);
                    File.Delete(Path.ChangeExtension(file, null) + "-log.db");
                }
            }
            Array.Sort(checkpoint);
            Array.Sort(conversion);
            Console.WriteLine($"encrypted={encrypted}, bytes={bytes}, checkpoint median={checkpoint[2]:F2} ms [{string.Join(", ", checkpoint.Select(x => x.ToString("F2")))}]");
            Console.WriteLine($"encrypted={encrypted}, conversion median={conversion[2]:F2} ms [{string.Join(", ", conversion.Select(x => x.ToString("F2")))}]");
        }
    }

    private static void MakeLegacyHeader(string file, string password)
    {
        // Page payloads are unchanged; legacy readers ignore the persisted
        // transaction field. Exercise automatic metadata conversion, not rebuild.
        using var raw = new FileStream(file, FileMode.Open, FileAccess.ReadWrite);
        using var encrypted = password == null ? null : new AesStream(password, raw);
        var stream = (Stream)encrypted ?? raw;
        var page = new byte[8192];
        var read = 0;
        while (read < page.Length)
        {
            var count = stream.Read(page, read, page.Length - read);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
        page[59] = 8;
        Array.Clear(page, 125, 4);
        stream.Position = 0;
        stream.Write(page, 0, page.Length);
        stream.Flush();
        raw.Flush(true);
    }
}
