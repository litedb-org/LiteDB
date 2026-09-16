using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class ConstraintWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var keys = Enumerable.Range(1, 1000).ToArray();
        var parameters = new BsonDocument
        {
            ["keys"] = new BsonArray(keys.Select(x => new BsonValue(x))),
            ["other"] = new BsonArray(Enumerable.Range(995, 1000).Select(x => new BsonValue(x)))
        };
        measure("constraints-in-range-linq", 5, i => rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= 990)
            .ToList().Sum(x => x.Id));
        measure("constraints-in-range-sql", 5, i => Read("Score IN @keys AND Score >= 990"));
        measure("constraints-in-in-sql", 5, i => Read("Score IN @keys AND Score IN @other"));
        measure("constraints-between-sql", 5, i => Read("Score BETWEEN 1 AND 20000 AND Score BETWEEN 10000 AND 10009"));
        measure("constraints-control-id", 1000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("constraints-control-combined", 500, i => rows.Query().Where(x => x.City == "City234" && x.Score >= 1000)
            .FirstOrDefault().Id);
        plans["constraints"] = rows.Query().Where(x => keys.Contains(x.Score) && x.Score >= 990).GetPlan().ToString();

        long Read(string predicate)
        {
            using var reader = db.Execute("SELECT $ FROM rows WHERE " + predicate, parameters);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }
    }
}
