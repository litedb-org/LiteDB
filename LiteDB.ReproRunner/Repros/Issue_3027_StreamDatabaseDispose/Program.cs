using System.Reflection;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_StreamDatabaseDispose;

/// <summary>
/// Regression since 5.0.21 fixed by PR #3027 (guard: StreamDatabaseDispose_Tests). Without a log stream
/// the WAL of <c>new LiteDatabase(stream)</c> lives only in memory, so for a writable stream that is not
/// a MemoryStream the constructor forced every commit into the stream by setting the CHECKPOINT pragma to
/// 1 (#2652), and Dispose set it back through the engine before disposing the engine. Side effects:
/// the pragma is stored in the file's header, so a use that ends without LiteDatabase.Dispose leaves
/// CHECKPOINT=1 for every later open; a second Dispose throws (the engine is already disposed); and
/// Dispose with an open transaction throws before the engine is disposed, so the transaction is never
/// rolled back. 5.0.21 did none of this. Fixed: the engine checkpoints each commit without changing the
/// stored pragma, and Dispose only disposes the engine.
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
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_StreamDatabaseDispose-" + Guid.NewGuid().ToString("N"));
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
        var (pragma, count) = AbandonedStreamUse(Path.Combine(directory, "abandoned.db"));
        host.SendLog($"After a stream use that ended without LiteDatabase.Dispose, a filename open finds CHECKPOINT={pragma} and {count} documents");
        var second = SecondDispose(Path.Combine(directory, "twice.db"));
        host.SendLog("Second Dispose: " + (second ?? "no exception"));
        var (transaction, kept) = DisposeWithOpenTransaction(Path.Combine(directory, "transaction.db"));
        host.SendLog("Dispose with an open transaction: " + (transaction ?? "no exception") + $"; a filename open then finds {kept}");

        var defects = new[] { pragma == 1, second != null, transaction != null };
        if (defects.All(x => x))
        {
            return (Reproduced, "REPRODUCED: new LiteDatabase(stream) left CHECKPOINT=1 in the database header, a second Dispose threw " +
                $"and Dispose with an open transaction threw (second Dispose: {second}; with an open transaction: {transaction})");
        }

        Require(!defects.Any(x => x), $"only some of the defects occurred: CHECKPOINT={pragma}, second Dispose: {second ?? "no exception"}, " +
            $"with an open transaction: {transaction ?? "no exception"}");
        Require(pragma == 1000 && count == 2, $"after the abandoned stream use: CHECKPOINT={pragma} and {count} documents, expected 1000 and 2");
        Require(kept == "_id 1 only", $"after Dispose with an open transaction the file holds {kept}, expected _id 1 only (the open insert rolled back)");
        return (Fixed, "FIXED: new LiteDatabase(stream) left the stored CHECKPOINT pragma at 1000 and kept every commit, a second Dispose " +
            "did not throw, and Dispose with an open transaction rolled it back and released the stream");
    }

    /// <summary>A stream use that ends without LiteDatabase.Dispose (process exit, or the caller only disposes its stream).</summary>
    private static (int Pragma, int Count) AbandonedStreamUse(string path)
    {
        using (var db = new LiteDatabase(path))
        {
            db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
            Require(db.CheckpointSize == 1000, $"a new database has CHECKPOINT={db.CheckpointSize}, expected the default 1000");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var abandoned = new LiteDatabase(stream);
        abandoned.GetCollection("items").Insert(new BsonDocument { ["_id"] = 2 });
        stream.Dispose();
        GC.SuppressFinalize(abandoned);

        using var reopened = new LiteDatabase(path);
        return (reopened.CheckpointSize, reopened.GetCollection("items").Count());
    }

    private static string? SecondDispose(string path)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        var db = new LiteDatabase(stream);
        db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
        db.Dispose();
        try
        {
            db.Dispose();
            return null;
        }
        catch (Exception error)
        {
            return $"{error.GetType().Name}: {error.Message}";
        }
    }

    /// <summary>The typical <c>using (db) { db.BeginTrans(); ...; throw; }</c> path.</summary>
    private static (string? Error, string Kept) DisposeWithOpenTransaction(string path)
    {
        string? failure = null;
        using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            var db = new LiteDatabase(stream);
            db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 1 });
            Require(db.BeginTrans(), "BeginTrans did not start a transaction");
            db.GetCollection("items").Insert(new BsonDocument { ["_id"] = 2 });
            try
            {
                db.Dispose();
            }
            catch (Exception error)
            {
                failure = $"{error.GetType().Name}: {error.Message}";
            }
        }

        using var reopened = new LiteDatabase(path);
        var ids = reopened.GetCollection("items").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).ToArray();
        return (failure, ids.SequenceEqual(new[] { 1 }) ? "_id 1 only" : $"_id [{string.Join(",", ids)}]");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
