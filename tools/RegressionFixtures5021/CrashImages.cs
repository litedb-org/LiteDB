using System;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB;

/// <summary>
/// Data files beside the WAL 5.0.21 left behind: copied while the writer held them open, or left
/// by a process killed with Environment.FailFast. Steps that ran as processes of their own
/// originally (every step that must not dispose the database) run as child processes here.
/// </summary>
internal static class CrashImages
{
    private const int PageSize = 8192;

    /// <summary>
    /// crash.db, crash-log.db: 100 documents {_id, value: 0} checkpointed, then 20 updates to
    /// value 7 and one insert (_id 100) committed to the WAL only; both files copied while the
    /// engine is open, and the process exits without disposing it.
    /// </summary>
    public static void WalCrash(string output)
    {
        var work = Program.WorkDirectory();
        try
        {
            Program.RunChild("wal-crash", work);
            foreach (var name in new[] { "crash.db", "crash-log.db" }) File.Copy(Path.Combine(work, name), Path.Combine(output, name));
            Check(output, "crash", "", "docs", 101, 21);
        }
        finally { Program.DeleteWorkDirectory(work); }
    }

    /// <summary>
    /// crash.db, crash-log.db (password "wal-secret", pragma CHECKPOINT 0): 60 documents
    /// checkpointed, then 10 updates to value 7 and 5 inserts committed to the WAL only; both files
    /// copied while the engine is open, then the engine is disposed.
    /// </summary>
    public static void EncryptedWalCrash(string output)
    {
        var work = Program.WorkDirectory();
        try
        {
            var f = Path.Combine(work, "encrypted.db");
            var db = new LiteDatabase($"Filename={f};Password=wal-secret");
            db.Pragma("CHECKPOINT", 0);
            var col = db.GetCollection("docs");
            col.EnsureIndex("value");
            col.Insert(Enumerable.Range(0, 60).Select(i => new BsonDocument { ["_id"] = i, ["value"] = 0 }));
            db.Checkpoint();
            col.Update(Enumerable.Range(0, 10).Select(i => new BsonDocument { ["_id"] = i, ["value"] = 7 }));
            col.Insert(Enumerable.Range(60, 5).Select(i => new BsonDocument { ["_id"] = i, ["value"] = 7 }));
            // process-crash image: copy both files while the database is open
            File.Copy(f, Path.Combine(output, "crash.db"));
            File.Copy(Path.Combine(work, "encrypted-log.db"), Path.Combine(output, "crash-log.db"));
            Console.WriteLine($"data {new FileInfo(Path.Combine(output, "crash.db")).Length} log {new FileInfo(Path.Combine(output, "crash-log.db")).Length}");
            db.Dispose();
            Check(output, "crash", ";Password=wal-secret", "docs", 65, 15);
        }
        finally { Program.DeleteWorkDirectory(work); }
    }

    /// <summary>
    /// c.db, c-log.db: collections "a" and "b" with 10 documents each; then a worker thread holds
    /// an explicit transaction open on "a" (300 inserts), the main thread commits 3 inserts into
    /// "b", and the process is killed with Environment.FailFast.
    /// </summary>
    public static void ConcurrentWalCrash(string output)
    {
        var work = Program.WorkDirectory();
        try
        {
            Program.RunChild("concurrent-create", work);
            Program.RunChild("concurrent-crash", work, crashes: true);
            foreach (var name in new[] { "c.db", "c-log.db" }) File.Copy(Path.Combine(work, name), Path.Combine(output, name));
            PrintWal(Path.Combine(output, "c.db"), Path.Combine(output, "c-log.db"));
            var copy = CopyPair(output, "c", work, "check");
            using var db = new LiteDatabase($"Filename={copy};Connection=direct");
            Console.WriteLine($"5.0.21: a={db.GetCollection("a").Count()} b={db.GetCollection("b").Count()} b>=100:{db.GetCollection("b").Count(Query.GTE("_id", 100))}");
        }
        finally { Program.DeleteWorkDirectory(work); }
    }

    /// <summary>
    /// foreign-log.db: the WAL of another database (10,000 documents of 1,000 characters in "big",
    /// then one insert into a new collection "fresh" committed with its header, and the process
    /// killed with Environment.FailFast). Its 14 MB data file is not part of the fixture.
    /// </summary>
    public static void ForeignWal(string output)
    {
        var work = Program.WorkDirectory();
        try
        {
            Program.RunChild("foreign-crash", work, crashes: true);
            PrintWal(Path.Combine(work, "c.db"), Path.Combine(work, "c-log.db"));
            File.Copy(Path.Combine(work, "c-log.db"), Path.Combine(output, "foreign-log.db"));
        }
        finally { Program.DeleteWorkDirectory(work); }
    }

    /// <summary>The steps that ran as processes of their own.</summary>
    public static int Child(string mode, string dir)
    {
        switch (mode)
        {
            case "wal-crash": return WalCrashChild(dir);
            case "concurrent-create": return ConcurrentCreate(Path.Combine(dir, "c.db"));
            case "concurrent-crash": return ConcurrentCrash(Path.Combine(dir, "c.db"));
            case "foreign-crash": return ForeignCrash(Path.Combine(dir, "c.db"));
            default: throw new ArgumentException("unknown child mode " + mode);
        }
    }

    private static int WalCrashChild(string dir)
    {
        var path = Path.Combine(dir, "src.db");
        var db = new LiteDatabase(path);
        var col = db.GetCollection("docs");
        col.EnsureIndex("value");
        for (var i = 0; i < 100; i++) col.Insert(new BsonDocument { ["_id"] = i, ["value"] = 0 });
        db.Checkpoint();
        for (var i = 0; i < 20; i++) col.Update(new BsonDocument { ["_id"] = i, ["value"] = 7 });
        col.Insert(new BsonDocument { ["_id"] = 100, ["value"] = 7 });
        File.Copy(path, Path.Combine(dir, "crash.db"), true);
        File.Copy(path.Replace(".db", "-log.db"), Path.Combine(dir, "crash-log.db"), true);
        Console.WriteLine("data " + new FileInfo(Path.Combine(dir, "crash.db")).Length + " log " + new FileInfo(Path.Combine(dir, "crash-log.db")).Length);
        Environment.Exit(0); // do not dispose
        return 0;
    }

    private static int ConcurrentCreate(string path)
    {
        using var db = new LiteDatabase($"Filename={path};Connection=direct");
        var a = db.GetCollection("a");
        var b = db.GetCollection("b");
        for (var i = 0; i < 10; i++)
        {
            a.Insert(new BsonDocument { ["_id"] = i, ["v"] = new string('a', 100) });
            b.Insert(new BsonDocument { ["_id"] = i, ["v"] = "b" });
        }
        return 0;
    }

    private static int ConcurrentCrash(string path)
    {
        // T1 (worker thread) holds an open explicit transaction on "a" that allocated many
        // new pages (kept in memory); T2 (main thread) commits small inserts into "b".
        var db = new LiteDatabase($"Filename={path};Connection=direct");
        var a = db.GetCollection("a");
        var b = db.GetCollection("b");
        var started = new ManualResetEventSlim();
        var t1 = new Thread(() =>
        {
            db.BeginTrans();
            for (var i = 100; i < 400; i++) a.Insert(new BsonDocument { ["_id"] = i, ["v"] = new string('x', 1000) });
            started.Set();
            Thread.Sleep(Timeout.Infinite);
        }) { IsBackground = true };
        t1.Start();
        started.Wait();
        for (var i = 100; i < 103; i++) b.Insert(new BsonDocument { ["_id"] = i, ["v"] = new string('y', 6000) });
        Thread.Sleep(2000);
        Console.WriteLine("crashing");
        Environment.FailFast("simulated crash after T2 committed while T1 is open");
        return 1;
    }

    private static int ForeignCrash(string path)
    {
        // A larger database (about 1,700 pages): its WAL commits a new collection's pages and header.
        using (var db0 = new LiteDatabase($"Filename={path};Connection=direct"))
        {
            var big = db0.GetCollection("big");
            big.InsertBulk(Enumerable.Range(0, 10000).Select(i => new BsonDocument { ["_id"] = i, ["v"] = new string('z', 1000) }));
        }
        var db = new LiteDatabase($"Filename={path};Connection=direct");
        db.GetCollection("fresh").Insert(new BsonDocument { ["_id"] = 1, ["v"] = "foreign" });
        Thread.Sleep(2000);
        Console.WriteLine("crashing");
        Environment.FailFast("simulated crash");
        return 1;
    }

    /// <summary>Copy a data/WAL pair so a check cannot change the fixture files.</summary>
    private static string CopyPair(string dir, string name, string work, string copy)
    {
        var data = Path.Combine(work, copy + ".db");
        File.Copy(Path.Combine(dir, name + ".db"), data, true);
        File.Copy(Path.Combine(dir, name + "-log.db"), Path.Combine(work, copy + "-log.db"), true);
        return data;
    }

    /// <summary>5.0.21 recovers the image: document count and how many carry value 7.</summary>
    private static void Check(string output, string name, string options, string collection, int count, int sevens)
    {
        var work = Program.WorkDirectory();
        try
        {
            var copy = CopyPair(output, name, work, "check");
            using var db = new LiteDatabase($"Filename={copy}{options}");
            var col = db.GetCollection(collection);
            var found = (col.Count(), col.Count(Query.EQ("value", 7)));
            Console.WriteLine($"5.0.21 recovers: count={found.Item1} value7={found.Item2}");
            if (found != (count, sevens)) throw new InvalidOperationException($"expected count={count} value7={sevens}");
        }
        finally { Program.DeleteWorkDirectory(work); }
    }

    /// <summary>Page ID, type, transaction and confirmation of every WAL page (plain files only).</summary>
    private static void PrintWal(string dataPath, string logPath)
    {
        var data = File.ReadAllBytes(dataPath);
        var log = File.ReadAllBytes(logPath);
        Console.WriteLine($"data pages {data.Length / PageSize} LastPageID {BitConverter.ToUInt32(data, 64)}; log pages {log.Length / PageSize}");
        for (var i = 0; i < log.Length / PageSize; i++)
        {
            var at = i * PageSize;
            var header = log[at + 4] == 1 ? $" LastPageID={BitConverter.ToUInt32(log, at + 64)} sameCreation={BitConverter.ToInt64(log, at + 68) == BitConverter.ToInt64(data, 68)}" : "";
            Console.WriteLine($"  {i}: page {BitConverter.ToUInt32(log, at)} type {log[at + 4]} tx {BitConverter.ToUInt32(log, at + 14)} confirmed {log[at + 18]}{header}");
        }
    }
}
