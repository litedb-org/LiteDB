using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_LostHeaderBesideWal;

/// <summary>
/// Defect fixed by PR #3027 (guards: HeaderFrame_Tests, HeaderFrameCrash_Tests). Commits go to the
/// WAL and reach the data file only at a checkpoint. A power loss can keep the WAL but not the data
/// file's header: storage that cannot sync with "Durable Commits=false" (#2242), or a device that
/// acknowledged a sync it never did, leaves a new data file empty or its header page never written back
/// (zeros). The known-bad engine initialized a new, empty database over an empty data file and its
/// recovery discarded every WAL frame, so every committed row was lost silently (it refused a
/// zeroed header page as "not a valid LiteDB database"). Fixed (decision 11 of
/// docs/decisions/durability-policy.md): every WAL generation starts with a header frame, a copy of the
/// data header, and the open restores a lost data header from it: every committed row is there.
///
/// Black box: a database with CHECKPOINT = 0 (so the commits stay in the WAL) and "Durable
/// Commits=false" takes 50 rows and an index and is closed. Then its data file alone is damaged as a
/// power loss would leave it (the WAL is never touched): emptied, its header page zeroed, or its first
/// sector zeroed, and the database is opened again.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int PageSize = 8192;
    private const int Rows = 50;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_LostHeaderBesideWal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host, directory);
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

    private static (int Code, string Summary) Run(ReproHostClient host, string directory)
    {
        // An empty data file first: the known-bad engine starts a new database over its WAL.
        foreach (var (damage, zeroed) in new[] { ("an empty data file", -1), ("a data file whose header page is zeros", PageSize), ("a data file whose first sector is zeros", 512) })
        {
            var path = Path.Combine(directory, zeroed.ToString(), "app.db");
            var connection = $"Filename={path};Durable Commits=false";
            var log = Path.Combine(Path.GetDirectoryName(path)!, "app-log.db");
            Write(path, connection, log);

            var data = File.ReadAllBytes(path);
            if (zeroed < 0) data = Array.Empty<byte>();
            else Array.Clear(data, 0, zeroed);
            File.WriteAllBytes(path, data);
            var wal = File.ReadAllBytes(log);
            host.SendLog($"Damaged: {damage} ({data.Length} bytes) beside its untouched WAL ({wal.Length} bytes)");

            int count, threes;
            string[] collections;
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                (count, threes, collections) = (rows.Count(), rows.Count(Query.EQ("value", 3)), db.GetCollectionNames().ToArray());
                host.SendLog($"Opened {damage}: {count} rows, {threes} with value 3, collections [{string.Join(", ", collections)}]");
                if (count == 0 && collections.Length == 0)
                {
                    var walLeft = File.Exists(log) ? new FileInfo(log).Length : 0;
                    Require(walLeft < wal.Length, $"the open found no rows but kept the WAL ({walLeft} bytes)");
                    return (Reproduced, $"REPRODUCED: the open of {damage} beside a WAL of committed frames created a new, empty database " +
                        $"and discarded every frame: 0 of {Rows} rows, the WAL cut from {wal.Length} to {walLeft} bytes");
                }
                Require(count == Rows && threes == 7, $"{damage}: {count} rows, {threes} with value 3 (expected {Rows} and 7)");
                rows.Insert(new BsonDocument { ["_id"] = Rows + 1, ["value"] = 3 });
            }
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                (count, threes) = (rows.Count(), rows.Count(Query.EQ("value", 3)));
                Require(count == Rows + 1 && threes == 8, $"{damage}, reopened after its restore: {count} rows, {threes} with value 3 (expected {Rows + 1} and 8)");
            }
        }

        return (Fixed, $"FIXED: an empty data file, or one whose header page or first sector is zeros, beside a WAL of committed frames: the open " +
            $"restored the header from the WAL with every one of the {Rows} rows and the index, and the database took and kept a new commit");
    }

    /// <summary>50 committed rows and an index, left in the WAL (CHECKPOINT = 0, so the close does not checkpoint).</summary>
    private static void Write(string path, string connection, string log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var db = new LiteDatabase(connection))
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            rows.Insert(Enumerable.Range(1, Rows).Select(id => new BsonDocument { ["_id"] = id, ["value"] = id % 7 }));
            rows.EnsureIndex("value");
        }
        Require(File.Exists(log) && new FileInfo(log).Length > 0, "the close checkpointed: the WAL holds no committed frame");
        Require(new FileInfo(path).Length == PageSize, $"the data file holds {new FileInfo(path).Length} bytes, not only its header page");

        // Control: the undamaged pair (a copy) holds every row; the data file holds only its header page, so they are in the WAL.
        var control = Path.Combine(Path.GetDirectoryName(path)!, "control");
        Directory.CreateDirectory(control);
        File.Copy(path, Path.Combine(control, "app.db"));
        File.Copy(log, Path.Combine(control, "app-log.db"));
        using (var db = new LiteDatabase($"Filename={Path.Combine(control, "app.db")};Durable Commits=false"))
        {
            var count = db.GetCollection("rows").Count();
            Require(count == Rows, $"the undamaged database holds {count} rows, expected {Rows}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
