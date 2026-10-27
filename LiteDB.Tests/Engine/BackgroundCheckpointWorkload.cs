using System;
using System.Linq;

namespace LiteDB.Tests.Engine
{
    // Linked into the child executable. Expected state is generated independently
    // from an external acknowledgement, never inferred from recovered row counts.
    internal static class BackgroundCheckpointWorkload
    {
        internal const int Count = 24;

        internal static BsonDocument[] Rows(int version) => Enumerable.Range(0, Count).Select(id => new BsonDocument
        {
            ["_id"] = id,
            ["value"] = version * 100 + id,
            ["payload"] = new string((char)('a' + id), 2400) + ":" + version + ":" + id
        }).ToArray();

        internal static void Seed(LiteDatabase db)
        {
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Rows(0));
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" });
            db.CheckpointSize = 1000;
            db.Checkpoint();
        }

        internal static void Verify(LiteDatabase db, int version)
        {
            var rows = db.GetCollection("rows");
            var actual = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
            var expected = Rows(version);
            if (actual.Length != expected.Length) throw new InvalidOperationException("Partial transaction: row count");
            var plan = rows.Query().Where(Query.EQ("value", version * 100)).GetPlan();
            if (plan["index"]["name"].AsString != "value")
                throw new InvalidOperationException("Secondary index is missing or not selected");
            for (var id = 0; id < Count; id++)
            {
                if (!actual[id].Equals(expected[id]) || !rows.FindById(id).Equals(expected[id]))
                    throw new InvalidOperationException("Incorrect document/payload at " + id);
                var indexed = rows.Find(Query.EQ("value", version * 100 + id)).ToArray();
                if (indexed.Length != 1 || !indexed[0].Equals(expected[id]))
                    throw new InvalidOperationException("Incorrect secondary index at " + id);
            }
            foreach (var absent in new[] { 0, 1, 2, 3, 99 }.Where(v => v != version))
                if (rows.Find(Query.Between("value", absent * 100, absent * 100 + Count - 1)).Any())
                    throw new InvalidOperationException("Stale or uncommitted secondary keys");
            if (db.GetCollection("sentinel").FindAll().Single().ToString() !=
                new BsonDocument { ["_id"] = 1, ["payload"] = "untouched" }.ToString())
                throw new InvalidOperationException("Unrelated sentinel changed");
        }
    }
}
