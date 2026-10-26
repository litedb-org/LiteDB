using System.Diagnostics;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")
            ?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(Path.GetTempPath(), "i3099-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        host.SendLog("database directory: " + directory);
        try
        {
            var outcomes = new List<bool>();
            foreach (var encrypted in new[] { false, true })
            foreach (var mode in new[] { "clean", "wal", "setter" })
            {
                var stale = Run(Path.Combine(directory, mode + encrypted + ".db"), encrypted, mode);
                if (mode == "clean" && stale) throw new InvalidOperationException("Clean-open control ignored TIMEOUT.");
                if (mode != "clean") outcomes.Add(stale);
            }
            if (outcomes.Distinct().Count() != 1) throw new InvalidOperationException("Mixed WAL outcomes.");
            var reproduced = outcomes[0];
            var message = reproduced ? "PROVEN_STALE_TIMEOUT: WAL-only and post-open TIMEOUT waits outlive the configured second."
                : "FIXED_VERIFIED: WAL-only and post-open TIMEOUT expire with real contention; clean opens and cold rows verified.";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            Directory.Delete(directory, true);
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Harness or unexpected database failure", new { Exception = error.ToString() });
            return 1;
        }
    }

    private static bool Run(string filename, bool encrypted, string mode)
    {
        var connection = new ConnectionString { Filename = filename, Password = encrypted ? "secret" : null };
        var log = Path.Combine(Path.GetDirectoryName(filename)!, Path.GetFileNameWithoutExtension(filename) + "-log.db");
        using (var seed = new LiteDatabase(connection))
        {
            seed.GetCollection("c").Insert(Row(1));
            seed.GetCollection("c").EnsureIndex("value");
            seed.GetCollection("sentinel").Insert(Row(42));
            seed.Checkpoint(); // Only the default 60 seconds has reached the data header.
            if (mode != "clean")
            {
                seed.CheckpointSize = 0;
                seed.Timeout = TimeSpan.FromSeconds(mode == "wal" ? 1 : 2);
            }
        }
        if (mode != "clean" && (!File.Exists(log) || new FileInfo(log).Length == 0))
            throw new InvalidOperationException("No WAL to restore.");
        using var db = new LiteDatabase(connection);
        if (mode != "wal") db.Timeout = TimeSpan.FromSeconds(1);
        if (db.Timeout != TimeSpan.FromSeconds(1)) throw new InvalidOperationException("Getter reports the wrong TIMEOUT.");
        if (!db.BeginTrans()) throw new InvalidOperationException("Holder Begin failed.");
        db.GetCollection("c").Insert(Row(2));
        using var attempting = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? failure = null;
        var elapsed = TimeSpan.Zero;
        var worker = new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                attempting.Set();
                if (!db.BeginTrans()) throw new InvalidOperationException("Contender Begin failed.");
                db.GetCollection("c").Insert(Row(3));
            }
            catch (Exception error) { failure = error; }
            finally
            {
                elapsed = clock.Elapsed;
                db.Rollback();
                finished.Set();
            }
        }) { IsBackground = true };
        worker.Start();
        bool completed;
        try
        {
            if (!attempting.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Contender did not start.");
            completed = finished.Wait(TimeSpan.FromSeconds(4));
        }
        finally
        {
            if (!db.Rollback()) throw new InvalidOperationException("Holder rollback failed.");
            if (!worker.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Contender cleanup blocked.");
        }
        Console.WriteLine($"mode={mode}, encrypted={encrypted}, completedBeforeRelease={completed}, elapsed={elapsed}, error={failure}");
        var stale = !completed && failure == null && elapsed >= TimeSpan.FromSeconds(3.5);
        if (!stale && !(completed && failure is LiteException error && error.ErrorCode == 120 &&
            elapsed >= TimeSpan.FromSeconds(0.8) && elapsed < TimeSpan.FromSeconds(3.5)))
            throw new InvalidOperationException("Unexpected contention outcome.", failure);
        db.Checkpoint();
        db.Dispose();
        using var verify = new LiteDatabase(connection);
        if (verify.Timeout != TimeSpan.FromSeconds(1) || verify.GetCollection("c").Count() != 1 ||
            verify.GetCollection("c").FindOne(Query.EQ("value", 1))?["_id"].AsInt32 != 1 ||
            verify.GetCollection("sentinel").FindById(42)?["value"].AsInt32 != 42)
            throw new InvalidOperationException("Rollback/checkpoint/reopen changed committed rows or pragmas.");
        return stale;
    }

    private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["value"] = id };
}
