using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class NestedWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, string filter)
    {
        if (filter != null && !"nested".StartsWith(filter, StringComparison.Ordinal) &&
            !filter.StartsWith("nested", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<ArrayRow>("arrays");
        rows.InsertBulk(Enumerable.Range(1, 4000).Select(i => new ArrayRow
        {
            Id = i, Offset = i % 10, Values = Enumerable.Range(0, 32).Reverse().ToArray()
        }));
        measure("nested-map-linq", 5, i => rows.Query().Select(x => new { Values = x.Values.Select(v => v + x.Offset).ToArray() })
            .ToList().Sum(x => (long)x.Values.Sum()));
        measure("nested-filter-map-linq", 5, i => rows.Query().Select(x => new { Values = x.Values.Where(v => v >= x.Offset).Select(v => v + 1).ToArray() })
            .ToList().Sum(x => (long)x.Values.Sum()));
        measure("nested-filter-sql", 5, i => Read("SELECT { values: ARRAY(Values[@ >= $.Offset]) } FROM arrays"));
        measure("nested-sort-sql", 5, i => Read("SELECT { values: ARRAY(SORT(Values => @)) } FROM arrays"));
        measure("nested-source-map-sql", 5, i => Read("SELECT { values: ARRAY(MAP(Values => @ + COUNT(*))) } FROM arrays"));
        measure("nested-flatten-map-control", 5, i => Read("SELECT { values: ARRAY(MAP(Values => ITEMS([@,@ + 1]))) } FROM arrays"));
        measure("nested-array-control", 5, i => rows.Query().ToList().Sum(x => (long)x.Values.Sum()));

        long Read(string sql)
        {
            using var reader = db.Execute(sql);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["values"].AsArray.Sum(x => (long)x.AsInt32);
            return sum;
        }
    }

    public class ArrayRow
    {
        public int Id { get; set; }
        public int Offset { get; set; }
        public int[] Values { get; set; }
    }
}
