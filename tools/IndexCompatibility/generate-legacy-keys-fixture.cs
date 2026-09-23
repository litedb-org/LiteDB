#:package LiteDB@5.0.21
// Regenerates LiteDB.Tests/Resources/IndexMigrationLegacyKeys_5_0_21.zip with the released engine:
//   dotnet run tools/IndexCompatibility/generate-legacy-keys-fixture.cs -- LiteDB.Tests/Resources/IndexMigrationLegacyKeys_5_0_21.zip
#nullable disable
using System;
using System.IO;
using System.IO.Compression;
using LiteDB;

var output = args[0];
var work = Path.Combine(Path.GetTempPath(), "gen-v11-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    if (File.Exists(output)) File.Delete(output);
    using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
    foreach (var password in new string[] { null, "secret" })
    {
        var suffix = password == null ? "" : "-encrypted";
        Add(zip, work, "stale" + suffix + ".db", password, Stale);
        Add(zip, work, "indexes" + suffix + ".db", password, Indexes);
    }
}
finally { Directory.Delete(work, true); }

static void Add(ZipArchive zip, string work, string name, string password, Action<LiteDatabase> fill)
{
    var file = Path.Combine(work, name);
    var cs = new ConnectionString { Filename = file, Password = password, Collation = new Collation("en-US/None") };
    using (var db = new LiteDatabase(cs))
    {
        fill(db);
        db.Checkpoint();
    }
    if (File.Exists(file.Replace(".db", "-log.db"))) throw new InvalidOperationException("log remained");
    zip.CreateEntryFromFile(file, name);
}

// Released Update keeps a node whose old key == the new value under decimal-rounded
// numbers and null-as-missing document equality, including the primary key.
static void Stale(LiteDatabase db)
{
    var rows = db.GetCollection("rows");
    rows.EnsureIndex("price", "$.price");
    rows.EnsureIndex("meta", "$.meta");
    rows.EnsureIndex("tags", "$.tags");
    rows.EnsureIndex("code", "$.code", true);
    rows.EnsureIndex("n", "$.n");
    rows.Insert(new BsonDocument { ["_id"] = 1, ["price"] = 19.99, ["meta"] = new BsonDocument { ["a"] = BsonValue.Null },
        ["tags"] = new BsonArray { 1 }, ["code"] = 0.1, ["n"] = 1 });
    rows.Insert(new BsonDocument { ["_id"] = 2, ["price"] = 5, ["meta"] = new BsonDocument { ["a"] = 1 },
        ["tags"] = new BsonArray { 2 }, ["code"] = 0.2, ["n"] = 2 });
    rows.Update(new BsonDocument { ["_id"] = 1, ["price"] = 19.99m, ["meta"] = new BsonDocument { ["b"] = BsonValue.Null },
        ["tags"] = new BsonArray { 1.0000000000000002 }, ["code"] = 0.1m, ["n"] = 1.0000000000000002 });

    var ids = db.GetCollection("ids");
    ids.Insert(new BsonDocument { ["_id"] = 0.1, ["v"] = 1 });
    ids.Insert(new BsonDocument { ["_id"] = 7, ["v"] = 7 });
    ids.Update(new BsonDocument { ["_id"] = 0.1m, ["v"] = 2 });
    ids.Update(new BsonDocument { ["_id"] = 7.0000000000000009, ["v"] = 8 });
}

// Every index shape whose v11 ordering differs from the released engine.
static void Indexes(LiteDatabase db)
{
    var people = db.GetCollection("people");
    people.EnsureIndex("name", "$.name");
    people.EnsureIndex("person", "$.person");
    people.EnsureIndex("score", "$.score");
    people.EnsureIndex("tags", "$.tags[*]");
    people.EnsureIndex("email", "$.email", true);
    people.EnsureIndex("lower", "LOWER($.name)");
    var names = new[] { "a", "B", "b", "A", "é", "Z", "aa", "ab" };
    var scores = new BsonValue[] { 1, 1.5, 2.25m, 3L, 0.1, 0.1m, -1, 1e20 };
    for (var i = 0; i < 64; i++)
    {
        var bytes = new byte[12];
        bytes[0] = (byte)(i * 37 % 256);
        bytes[11] = (byte)i;
        var name = names[i % names.Length];
        people.Insert(new BsonDocument
        {
            ["_id"] = new ObjectId(bytes),
            ["name"] = name,
            ["person"] = new BsonDocument { ["first"] = names[(i + 3) % names.Length], ["age"] = i % 5 },
            ["score"] = scores[i % scores.Length],
            ["tags"] = new BsonArray { names[i % 3], names[(i + 1) % 4] },
            ["email"] = "user" + i + "@example.com"
        });
    }
    var numbers = db.GetCollection("numbers");
    for (var i = 0; i < 40; i++)
        numbers.Insert(new BsonDocument { ["_id"] = scores[i % scores.Length].IsDouble ? (BsonValue)(i + 0.5) : i, ["x"] = scores[(i * 3) % scores.Length] });
}
