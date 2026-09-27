using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteDB;

namespace V8Differential;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("action file snapshot seed [operations] [password]");
        var action = args[0];
        var file = args[1];
        var snapshot = args[2];
        var seed = int.Parse(args[3]);
        var operations = args.Length > 4 ? int.Parse(args[4]) : 0;
        var password = args.Length > 5 ? args[5] : null;
        if (action == "create") Create(file, snapshot, seed, password);
        else if (action == "mutate") Mutate(file, snapshot, seed, operations, password);
        else if (action == "verify") Verify(file, snapshot, password);
        else if (action == "reject") Reject(file, password);
        else if (action == "needs-migration") NeedsMigration(file, password);
        else throw new ArgumentException("Unknown action " + action);
        Console.WriteLine($"{typeof(LiteDatabase).Assembly.GetName().Version}: {action} passed for {Path.GetFileName(file)}");
        return 0;
    }

    private static void NeedsMigration(string file, string password)
    {
        try
        {
            using var db = Open(file, password, readOnly: true);
            throw new InvalidDataException("Legacy read-only open must request index migration.");
        }
        catch (LiteException error) when (error.Message.Contains("index ordering/collation requires migration")) { }
    }

    private static void Create(string file, string snapshot, int seed, string password)
    {
        File.Delete(file);
        using (var db = Open(file, password))
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            rows.EnsureIndex("tags", "Tags[*]");
            for (var id = 1; id <= 40; id++) rows.Insert(Document(id, Mix(seed, id)));
            db.Checkpoint();
        }
        Save(file, snapshot, password);
    }

    private static void Mutate(string file, string snapshot, int seed, int operations, string password)
    {
        using (var db = Open(file, password))
        {
            var rows = db.GetCollection("rows");
            for (var operation = 0; operation < operations; operation++)
            {
                var mixed = Mix(seed, operation);
                var id = 1 + (int)(mixed % 80);
                if (mixed % 5 == 0) rows.Delete(id);
                else rows.Upsert(Document(id, Mix(seed ^ 0x51ed270b, operation)));
            }
            db.Checkpoint();
        }
        Save(file, snapshot, password);
    }

    private static void Verify(string file, string snapshot, string password)
    {
        var expected = System.Text.Json.JsonSerializer.Deserialize<SnapshotRow[]>(File.ReadAllText(snapshot));
        using var db = Open(file, password, readOnly: true);
        var actual = Rows(db).ToArray();
        if (!expected.SequenceEqual(actual)) throw new InvalidDataException("Cross-version logical snapshot mismatch.");
        var indexed = db.GetCollection("rows").Query().OrderBy("Value").ToArray()
            .Select(MapRow).ToArray();
        if (!indexed.Select(row => row.Id).OrderBy(id => id).SequenceEqual(actual.Select(row => row.Id)) ||
            !indexed.Select(row => row.Value).SequenceEqual(indexed.Select(row => row.Value).OrderBy(value => value)))
            throw new InvalidDataException("Cross-version secondary-index order mismatch.");
    }

    private static void Save(string file, string snapshot, string password)
    {
        using var db = Open(file, password, readOnly: true);
        File.WriteAllText(snapshot, System.Text.Json.JsonSerializer.Serialize(Rows(db),
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<SnapshotRow> Rows(LiteDatabase db) => db.GetCollection("rows").Query()
        .OrderBy("_id").ToArray().Select(MapRow);

    private static SnapshotRow MapRow(BsonDocument document) => new(document["_id"].AsInt32,
        document["Value"].AsInt32, document["Text"].AsString,
        string.Join(",", document["Tags"].AsArray.Select(value => value.AsInt32)),
        Convert.ToBase64String(document["Payload"].AsBinary));

    private static BsonDocument Document(int id, uint mixed) => new()
    {
        ["_id"] = id,
        ["Value"] = unchecked((int)(mixed % 2001) - 1000),
        ["Text"] = $"row-{id}-{mixed % 97}",
        ["Tags"] = new BsonArray((int)(mixed % 11), (int)((mixed >> 8) % 13)),
        ["Payload"] = BitConverter.GetBytes(mixed)
    };

    private static LiteDatabase Open(string file, string password, bool readOnly = false) => new(new ConnectionString
    {
        Filename = file,
        Password = password,
        ReadOnly = readOnly
    });

    private static void Reject(string file, string password)
    {
        var before = File.ReadAllBytes(file);
        var log = Path.ChangeExtension(file, null) + "-log.db";
        var beforeLog = File.Exists(log) ? File.ReadAllBytes(log) : Array.Empty<byte>();
        foreach (var readOnly in new[] { true, false })
        {
            var rejected = false;
            try
            {
                using var db = Open(file, password, readOnly);
                db.GetCollection("rows").Count();
            }
            catch (LiteException error) when (error.ErrorCode == LiteException.INVALID_DATABASE) { rejected = true; }
            var afterLog = File.Exists(log) ? File.ReadAllBytes(log) : Array.Empty<byte>();
            if (!rejected || !before.SequenceEqual(File.ReadAllBytes(file)) || !beforeLog.SequenceEqual(afterLog))
                throw new InvalidDataException("Released engine must reject checksum files without changing data or WAL.");
        }
    }

    private static uint Mix(int seed, int value)
    {
        var result = unchecked((uint)seed + (uint)value * 0x9e3779b9u);
        result ^= result >> 16;
        result *= 0x7feb352d;
        result ^= result >> 15;
        result *= 0x846ca68b;
        return result ^ (result >> 16);
    }

    private sealed record SnapshotRow(int Id, int Value, string Text, string Tags, string Payload);
}
