using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class InteropBenchmarks
{
    // Both old and new production DLLs execute this identical public-API protocol.
    internal static void Run(string filename)
    {
        using var db = new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared, TransactionPageLimit = 3 });
        var rows = db.GetCollection("rows");
        var readers = new Dictionary<int, IEnumerator<BsonDocument>>();
        try
        {
            string command;
            while ((command = Console.ReadLine()) != null && command != "exit")
            {
                var parts = command.Split(' ');
                var generation = parts.Length > 1 ? int.Parse(parts[1]) : 0;
                switch (parts[0])
                {
                    case "write":
                        db.BeginTrans();
                        rows.EnsureIndex("revision", "revision");
                        for (var id = 0; id < 200; id++)
                        {
                            rows.Delete(id);
                            rows.Insert(Document(id, generation));
                        }
                        db.GetCollection("witness").Upsert(new BsonDocument { ["_id"] = 1, ["revision"] = generation });
                        db.Commit();
                        break;
                    case "open":
                        var reader = rows.Query().OrderBy("_id").ToEnumerable().GetEnumerator();
                        if (!reader.MoveNext()) throw new InvalidOperationException("Empty snapshot");
                        Validate(reader.Current, 0, generation);
                        readers.Add(generation, reader);
                        break;
                    case "release":
                        using (var held = readers[generation])
                        {
                            var id = 1;
                            while (held.MoveNext()) Validate(held.Current, id++, generation);
                            if (id != 200) throw new InvalidOperationException("Snapshot count changed");
                        }
                        readers.Remove(generation);
                        break;
                    case "verify":
                        Verify(rows, db, generation);
                        break;
                    case "verify-either":
                        var actual = db.GetCollection("witness").FindById(1)["revision"].AsInt32;
                        if (actual != generation && actual != int.Parse(parts[2]))
                            throw new InvalidOperationException("No legal concurrent transaction won");
                        Verify(rows, db, actual);
                        break;
                    case "uncommitted":
                        db.BeginTrans();
                        if (rows.DeleteAll() != 200 || db.GetCollection("witness").DeleteAll() != 1)
                            throw new InvalidOperationException("Incomplete uncommitted deletion");
                        Console.WriteLine(command);
                        // The controller kills this native writer with its transaction open.
                        Console.ReadLine();
                        throw new InvalidOperationException("Crash worker was not terminated");
                    case "rollback":
                        db.BeginTrans();
                        rows.DeleteAll();
                        db.GetCollection("witness").DeleteAll();
                        db.Rollback();
                        break;
                    case "checkpoint": db.Checkpoint(); break;
                    default: throw new ArgumentException(command);
                }
                Console.WriteLine(command);
            }
        }
        finally { foreach (var reader in readers.Values) reader.Dispose(); }
    }

    private static void Verify(ILiteCollection<BsonDocument> rows, LiteDatabase db, int generation)
    {
        var current = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
        if (current.Length != 200) throw new InvalidOperationException("Current count changed");
        for (var id = 0; id < current.Length; id++) Validate(current[id], id, generation);
        var indexed = rows.Find(Query.EQ("revision", generation)).OrderBy(x => x["_id"].AsInt32).ToArray();
        if (indexed.Length != 200) throw new InvalidOperationException("Index count changed");
        for (var id = 0; id < indexed.Length; id++) Validate(indexed[id], id, generation);
        if (db.GetCollection("witness").FindById(1)["revision"].AsInt32 != generation)
            throw new InvalidOperationException("Atomic witness changed");
    }

    private static BsonDocument Document(int id, int generation) => new BsonDocument
    {
        ["_id"] = id, ["revision"] = generation, ["payload"] = new string((char)('a' + id % 26), 4000) + ":" + generation
    };

    private static void Validate(BsonDocument row, int id, int generation)
    {
        var expected = Document(id, generation);
        if (row.Count != expected.Count || row["_id"] != expected["_id"] ||
            row["revision"] != expected["revision"] || row["payload"] != expected["payload"])
            throw new InvalidOperationException("Changed payload at " + id + "/" + generation);
    }
}
