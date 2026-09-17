using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class NodeLinkWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        measure("links-primary-count", 20, i => rows.Count());
        measure("links-secondary-range-count", 20, i => rows.Count(x => x.Score >= 10000));
        measure("links-exclusion-count", 20, i => rows.Count(x => x.Score != 10000));
        measure("links-primary-lookup", 4000, i =>
        {
            var id = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == id).FirstOrDefault().Id;
        });
        measure("links-secondary-projection", 2000, i => rows.Query().Where(x => x.City == "City234")
            .Select(x => new { x.Id, x.Name }).Limit(5).ToList().Sum(x => x.Id));
        measure("links-full-scan", 5, i => rows.Query().Where(x => x.Name.StartsWith("Person1")).ToList().Sum(x => x.Id));
        measure("links-insert-query-delete-control", 300, i =>
        {
            rows.Insert(new Program.Row { Id = 30000, Score = 30000, Name = "Inserted", City = "Inserted" });
            var result = rows.FindById(30000).Id;
            if (!rows.Delete(30000)) throw new InvalidOperationException("Inserted row was not deleted");
            return result;
        });
        measure("links-update-query-restore-control", 300, i =>
        {
            var original = rows.FindById(1234);
            var changed = new Program.Row { Id = original.Id, Score = 30000, Name = original.Name, City = original.City };
            if (!rows.Update(changed)) throw new InvalidOperationException("Row was not updated");
            var result = rows.Count(x => x.Score == 30000);
            if (!rows.Update(original)) throw new InvalidOperationException("Row was not restored");
            return result;
        });
    }
}
