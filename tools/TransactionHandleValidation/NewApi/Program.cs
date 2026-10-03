using LiteDB;
using LiteDB.Engine;

// The migration target for CS0618: compiled with TreatWarningsAsErrors=true against the
// candidate, so any warning from the replacement API fails the build.
Console.WriteLine($"loaded={typeof(LiteDatabase).Assembly.Location}");
var directory = Path.Combine(Path.GetTempPath(), "litedb-handle-newapi-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using (var db = new LiteDatabase(":memory:"))
    {
        CommitAndRollBack(db, "memory");
    }

    using (var owned = new LiteEngine(new EngineSettings { Filename = ":memory:" }))
    {
        using (var facade = new LiteDatabase(owned, disposeOnClose: false))
        {
            CommitAndRollBack(facade, "caller-owned");
        }

        using var again = new LiteDatabase(owned, disposeOnClose: false);
        Check(again.GetCollection("rows").Count() == 1, "caller-owned engine lost the handle's commit");
    }

    using (var db = new LiteDatabase(Path.Combine(directory, "direct.db")))
    {
        CommitAndRollBack(db, "direct-file");
    }

    var shared = $"Filename={Path.Combine(directory, "shared.db")};Connection=Shared";
    using (var db = new LiteDatabase(shared))
    using (var peer = new LiteDatabase(shared))
    {
        CommitAndRollBack(db, "shared");
        Check(peer.GetCollection("rows").Count() == 1, "shared peer does not see the handle's commit");
    }
}
finally
{
    Directory.Delete(directory, true);
}

Console.WriteLine("PASS new transaction API consumer");

static void CommitAndRollBack(ILiteDatabase db, string name)
{
    using (var transaction = db.BeginTransaction())
    {
        transaction.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
        // No ordinary call here: in Shared mode it would wait for the writer mutex this flow holds.
        Check(transaction.GetCollection("rows").Count() == 1, $"{name}: the handle does not see its own write");
        transaction.Commit();
        Check(transaction.State == LiteTransactionState.Committed, $"{name}: commit state {transaction.State}");
    }

    using (var transaction = db.BeginTransaction())
    {
        transaction.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
        transaction.Rollback();
        Check(transaction.State == LiteTransactionState.RolledBack, $"{name}: rollback state {transaction.State}");
    }

    Check(db.GetCollection("rows").Count() == 1, $"{name}: expected exactly the committed row");
    Console.WriteLine($"{name}=committed,rolled-back");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
