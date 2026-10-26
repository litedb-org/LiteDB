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
        var directory = Path.Combine(Path.GetTempPath(), "i3094-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        host.SendLog("database directory: " + directory);
        try
        {
            var outcomes = new List<bool>();
            foreach (var encrypted in new[] { false, true })
            {
                if (Run(Path.Combine(directory, "control-" + encrypted + ".db"), encrypted, false))
                    throw new InvalidOperationException("Ordinary transaction control blocked.");
                outcomes.Add(Run(Path.Combine(directory, "retry-" + encrypted + ".db"), encrypted, true));
            }
            if (outcomes.Distinct().Count() != 1) throw new InvalidOperationException("Mixed retry outcomes.");
            var reproduced = outcomes[0];
            var message = reproduced ? "PROVEN_BLOCK: retry Commit retains writer ownership in plain and encrypted files."
                : "FIXED_VERIFIED: retry Commit admits same-instance and peer writers; committed rows preserved.";
            Console.WriteLine(message);
            host.SendResult(reproduced, message);
            if (!reproduced) Directory.Delete(directory, true);
            // The baseline workers retain live engines. Process exit, without disposal,
            // ends them; disposing would conceal the ownership leak under test.
            return reproduced ? 0 : 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            host.SendResult(false, "Harness or unexpected database failure", new { Exception = error.ToString() });
            return 1;
        }
    }

    private static bool Run(string filename, bool encrypted, bool retry)
    {
        var connection = new ConnectionString
        {
            Filename = filename, Connection = ConnectionType.Shared, Password = encrypted ? "secret" : null
        };
        var db = new LiteDatabase(connection);
        var rows = db.GetCollection("c");
        rows.Insert(Row(1));
        if (!db.BeginTrans()) throw new InvalidOperationException("First Begin failed.");
        if (retry)
        {
            try
            {
                rows.Insert(Row(1));
                throw new InvalidOperationException("Duplicate insert unexpectedly succeeded.");
            }
            catch (LiteException error) when (error.ErrorCode == 110) { }
            if (!db.BeginTrans()) throw new InvalidOperationException("Retry Begin failed.");
        }
        if (!db.Commit()) throw new InvalidOperationException("Commit failed.");
        Exception? failure = null;
        using var entered = new ManualResetEventSlim();
        using var peerWriteEntered = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                entered.Set();
                using var peer = new LiteDatabase(connection);
                peerWriteEntered.Set();
                peer.GetCollection("c").Insert(Row(3));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        var clock = Stopwatch.StartNew();
        worker.Start();
        if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Worker did not start.");
        if (!peerWriteEntered.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Worker did not reach peer write.");
        var completed = worker.Join(TimeSpan.FromSeconds(3));
        Console.WriteLine($"retry={retry}, encrypted={encrypted}, completed={completed}, elapsed={clock.Elapsed}");
        if (!completed) return true;
        if (failure != null) throw new InvalidOperationException("Writer failed", failure);
        if (rows.Count(Query.EQ("_id", 1)) != 1) throw new InvalidOperationException("Incorrect committed row count.");
        rows.Insert(Row(2));
        if (db.Commit() || db.Rollback()) throw new InvalidOperationException("Phantom transaction remains.");
        db.Dispose();
        using var verify = new LiteDatabase(new ConnectionString { Filename = filename, Password = connection.Password });
        if (!verify.GetCollection("c").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).SequenceEqual(new[] { 1, 2, 3 }))
            throw new InvalidOperationException("Committed rows changed after reopen.");
        return false;
    }

    private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id };
}
