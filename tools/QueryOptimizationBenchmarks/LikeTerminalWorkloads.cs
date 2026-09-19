using System;
using System.Linq;
using LiteDB;

internal static class LikeTerminalWorkloads
{
    internal static void Run(Action<string, int, Func<int, long>> measure, string filter)
    {
        if (filter != null && !filter.StartsWith("liketail", StringComparison.Ordinal) &&
            !"liketail".StartsWith(filter, StringComparison.Ordinal)) return;
        using var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection<Program.Row>("rows");
        var padding = new string('x', 256);
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Program.Row
        {
            Id = i, Name = "Record-" + i + "-" + padding + "-Tail"
        }));
        const long allIds = 200010000;
        measure("liketail-long-prefix-linq", 2, i => Check(rows.Query().Where(x => x.Name.StartsWith("Record"))
            .Select(x => x.Id).ToList().Sum(x => (long)x), allIds));
        measure("liketail-long-contains-linq", 2, i => Check(rows.Query().Where(x => x.Name.Contains("Record"))
            .Select(x => x.Id).ToList().Sum(x => (long)x), allIds));
        measure("liketail-long-prefix-sql", 2, i => Check(Read("Record%"), allIds));
        measure("liketail-all-strings-sql", 2, i => Check(Read("%"), allIds));
        measure("liketail-late-match-control", 2, i => Check(rows.Query().Where(x => x.Name.Contains("Tail"))
            .Select(x => x.Id).ToList().Sum(x => (long)x), allIds));
        measure("liketail-nonmatch-control", 2, i => Check(rows.Query().Where(x => x.Name.Contains("Absent"))
            .Select(x => x.Id).ToList().Sum(x => (long)x), 0));

        long Read(string pattern)
        {
            using var reader = db.Execute("SELECT _id FROM rows WHERE Name LIKE @pattern", new BsonDocument { ["pattern"] = pattern });
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }

        long Check(long result, long expected)
        {
            if (result != expected) throw new InvalidOperationException("LIKE query returned unexpected fixture rows.");
            return result;
        }
    }
}
