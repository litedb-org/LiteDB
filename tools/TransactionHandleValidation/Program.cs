using System.Reflection;
using LiteDB;
using LiteDB.Engine;

// Compiled once against the parent LiteDB.dll, then run unchanged against the candidate DLL.
// Only the pre-handle API is referenced at compile time; new APIs are reached by reflection.
var scenario = args.Length > 0 ? args[0] : "";
var library = typeof(LiteDatabase).Assembly;
Console.WriteLine($"loaded={library.Location}");
Console.WriteLine($"version={library.GetName().Version}");

switch (scenario)
{
    case "binary":
        Binary();
        Console.WriteLine("PASS precompiled old ILiteDatabase implementation and legacy consumer");
        return 0;
    case "parity":
        LegacyParity.Run();
        Console.WriteLine("PASS legacy outcome transcript written");
        return 0;
    default:
        Console.Error.WriteLine("usage: TransactionHandleValidation binary|parity");
        return 2;
}

static void Binary()
{
    using (ILiteDatabase db = new LiteDatabase(":memory:"))
    {
        Check(db.BeginTrans(), "begin failed");
        db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        Check(db.Commit() && db.GetCollection("rows").Count() == 1, "commit failed");
    }

    // Caller-owned engine: the facade must not dispose it, and a later facade sees the commit.
    using (var owned = new LiteEngine(new EngineSettings { Filename = ":memory:" }))
    {
        using (var facade = new LiteDatabase(owned, disposeOnClose: false))
        {
            Check(facade.BeginTrans(), "owned begin failed");
            facade.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            Check(facade.Commit(), "owned commit failed");
        }

        using (var again = new LiteDatabase(owned, disposeOnClose: false))
        {
            Check(again.GetCollection("rows").Count() == 1, "caller-owned engine lost its commit or was disposed");
        }
    }

    using var mock = new MockDatabase();
    using var raw = new LiteEngine(new EngineSettings { Filename = ":memory:" });
    var decorator = new LegacyEngineDecorator(raw);
    using (var decorated = new LiteDatabase(decorator, disposeOnClose: false))
    {
        Check(decorated.BeginTrans(), "decorator begin failed");
        decorated.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
        Check(decorated.Commit() && decorated.GetCollection("rows").Count() == 1, "decorator failed");
        RefuseHandles(mock, decorated, raw);
    }

    Check(!decorator.Disposed, "disposeOnClose: false disposed the caller's decorator");
    using (var reopened = new LiteDatabase(raw, disposeOnClose: false))
    {
        Check(reopened.GetCollection("rows").Count() == 1, "decorated engine lost its commit");
    }
}

// Reflection keeps this fixture compilable against the parent, which has no handle API.
static void RefuseHandles(ILiteDatabase mock, ILiteDatabase decorated, LiteEngine raw)
{
    var extensions = typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteTransactionExtensions");
    var begin = extensions?.GetMethod("BeginTransaction", new[] { typeof(ILiteDatabase) });
    if (begin == null)
    {
        Console.WriteLine("handle-api=absent");
        return;
    }

    Console.WriteLine("handle-api=present");
    var controlled = extensions.GetMethod("BeginTransaction",
        new[] { typeof(ILiteDatabase), typeof(TimeSpan), typeof(CancellationToken) });
    foreach (var unsupported in new[] { mock, decorated })
    {
        ExpectNotSupported(() => begin.Invoke(null, new object[] { unsupported }));

        // Absent on #3064's first slice; checked if a later revision adds it.
        if (controlled != null)
        {
            ExpectNotSupported(() => controlled.Invoke(null, new object[] { unsupported, TimeSpan.Zero, CancellationToken.None }));
        }
    }

    // A refusal must have no side effect: no legacy transaction may have started.
    Check(raw.BeginTrans(), "capability rejection started a legacy transaction");
    raw.Rollback();
    Console.WriteLine("handle-refusal=NotSupportedException before side effects");
}

static void ExpectNotSupported(Action begin)
{
    try
    {
        begin();
    }
    catch (TargetInvocationException error) when (error.InnerException is NotSupportedException)
    {
        return;
    }

    throw new Exception("unsupported capability accepted");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
