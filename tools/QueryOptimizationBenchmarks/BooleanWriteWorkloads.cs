using System;
using System.Linq;
using LiteDB;

internal static class BooleanWriteWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, string filter)
    {
        if (filter == null || !filter.StartsWith("boolwrite", StringComparison.Ordinal)) return;
        var rows = db.GetCollection("rows");
        var unique = db.GetCollection("writeUnique");
        unique.InsertBulk(Enumerable.Range(1, 20000).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i, ["Name"] = "Person" + i }));
        unique.EnsureIndex("score", "Score", true);
        const string range = "Score >= 1000 AND Score < 1100";
        measure("boolwrite-secondary-range-control", 100, i => Update(rows, range, 100));
        measure("boolwrite-unique-range-control", 100, i => Update(unique, range, 100));
        measure("boolwrite-primary-range-control", 100, i => Update(rows, "_id >= 1000 AND _id < 1100", 100));
        measure("boolwrite-nested", 10, i => Update(rows,
            "(Score BETWEEN 1000 AND 15009 AND (Score < 1010 OR Score >= 15000)) OR Score = 17890", 21));
        measure("boolwrite-secondary-delete-control", 100, i => Delete(range));
        measure("boolwrite-primary-delete-control", 100, i => Delete("_id >= 1000 AND _id < 1100"));

        long Delete(string predicate)
        {
            db.BeginTrans();
            try
            {
                var changed = rows.DeleteMany(predicate);
                if (changed != 100 || rows.FindById(1000) != null)
                    throw new InvalidOperationException("Unexpected delete result");
                return changed;
            }
            finally
            {
                db.Rollback();
            }
        }

        long Update(ILiteCollection<BsonDocument> collection, string predicate, int expected)
        {
            db.BeginTrans();
            try
            {
                // Change a non-indexed field so both assemblies produce correct
                // results. Include rollback to start each call from the same data.
                var changed = collection.UpdateMany("{ Name: 'updated' }", predicate);
                if (changed != expected || collection.FindById(1000)["Name"].AsString != "updated")
                    throw new InvalidOperationException("Unexpected update result");
                return changed;
            }
            finally
            {
                db.Rollback();
            }
        }
    }
}
