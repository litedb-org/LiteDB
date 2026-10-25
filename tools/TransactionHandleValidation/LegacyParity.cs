using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LiteDB;
using LiteDB.Engine;

/// <summary>
/// Old-API behavior written as a transcript of <c>parity:</c> lines. The script requires the
/// parent and the candidate to print the same transcript: #3064 keeps the legacy API's
/// thread-bound semantics and ordinary statement-error behavior unchanged. Exceptions are
/// reported by their nearest public type, which is what an existing caller can catch.
/// </summary>
public static class LegacyParity
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-handle-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Memory();
            Errors();
            ForeignThread();
            Engine();
            Files(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void Memory()
    {
        using var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection("rows");
        Emit("nested", Outcome(() => db.BeginTrans()), Outcome(() => db.BeginTrans()),
            Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 1 })),
            Outcome(() => db.Commit()), Outcome(() => db.Commit()), Outcome(() => db.Rollback()), rows.Count());
        Emit("rollback", Outcome(() => db.BeginTrans()), Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 2 })),
            rows.Count(), Outcome(() => db.Rollback()), rows.Count());
    }

    private static void Errors()
    {
        using var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection("rows");
        Emit("ordinary-error", Outcome(() => rows.Insert(Failing(10))), rows.Count());

        Emit("legacy-error", Outcome(() => db.BeginTrans()), Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 20 })),
            Outcome(() => rows.Insert(Failing(21))), Outcome(() => rows.Count()), Outcome(() => db.Commit()),
            Outcome(() => db.Rollback()), rows.Count(), rows.FindById(20) != null, rows.FindById(21) != null);

        Emit("duplicate-key", Outcome(() => db.BeginTrans()), Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 30 })),
            Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 30 })), Outcome(() => rows.Count()),
            Outcome(() => db.Commit()), Outcome(() => db.Rollback()), rows.Count());
    }

    private static void ForeignThread()
    {
        using var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection("rows");
        using var begun = new ManualResetEventSlim();
        using var checkedByPeer = new ManualResetEventSlim();
        string owner = null;
        var thread = new Thread(() =>
        {
            owner = Outcome(() => db.BeginTrans()) + "," + Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 1 }));
            begun.Set();
            checkedByPeer.Wait();
            owner += "," + Outcome(() => db.Rollback());
        });
        thread.Start();
        begun.Wait();
        var peer = Outcome(() => db.Commit()) + "," + Outcome(() => db.Rollback());
        checkedByPeer.Set();
        thread.Join();
        Emit("foreign-thread", owner, peer, rows.Count());
    }

    private static void Engine()
    {
        using var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
        Emit("engine", Outcome(() => engine.BeginTrans()), Outcome(() => engine.BeginTrans()),
            Outcome(() => engine.Insert("rows", new[] { new BsonDocument { ["_id"] = 1 } }, BsonAutoId.Int32)),
            Outcome(() => engine.Rollback()), Outcome(() => engine.Rollback()), Outcome(() => engine.Commit()));
    }

    private static void Files(string directory)
    {
        var direct = Path.Combine(directory, "direct.db");
        using (var db = new LiteDatabase(direct))
        {
            var rows = db.GetCollection("rows");
            Emit("direct-file", Outcome(() => db.BeginTrans()), Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 1 })),
                Outcome(() => db.Commit()));
        }

        using (var db = new LiteDatabase($"Filename={direct};ReadOnly=true"))
        {
            var rows = db.GetCollection("rows");
            Emit("read-only", rows.Count(), Outcome(() => db.BeginTrans()),
                Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 2 })), Outcome(() => db.Rollback()), rows.Count());
        }

        var shared = $"Filename={Path.Combine(directory, "shared.db")};Connection=Shared";
        using (var first = new LiteDatabase(shared))
        using (var second = new LiteDatabase(shared))
        {
            var rows = first.GetCollection("rows");
            Emit("shared", Outcome(() => first.BeginTrans()), Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 1 })),
                Outcome(() => first.Commit()), second.GetCollection("rows").Count(),
                Outcome(() => first.BeginTrans()), Outcome(() => rows.Insert(new BsonDocument { ["_id"] = 2 })),
                Outcome(() => first.Rollback()), second.GetCollection("rows").Count());
        }

        using (var reopened = new LiteDatabase(direct))
        {
            Emit("reopen", reopened.GetCollection("rows").Count());
        }
    }

    private static IEnumerable<BsonDocument> Failing(int id)
    {
        yield return new BsonDocument { ["_id"] = id };
        throw new InvalidOperationException("input failed");
    }

    private static string Outcome(Func<object> action)
    {
        try
        {
            return Convert.ToString(action(), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception error)
        {
            var type = error.GetType();
            while (!type.IsPublic) type = type.BaseType;
            var code = error is LiteException lite ? ":" + lite.ErrorCode : "";
            return "throw " + type.FullName + code;
        }
    }

    private static void Emit(string name, params object[] values)
    {
        Console.WriteLine($"parity:{name}={string.Join(",", values)}");
    }
}
