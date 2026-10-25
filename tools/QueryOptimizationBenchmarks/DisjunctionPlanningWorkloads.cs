using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

internal static class DisjunctionPlanningWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure,
        Dictionary<string, string> plans, string filter)
    {
        if (filter == null || !filter.StartsWith("orplan", StringComparison.Ordinal)) return;
        var rows = db.GetCollection<Program.Row>("rows");
        Query("orplan-id-linq", () => rows.Query().Where(x => x.Id == 1234 &&
            ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99)));
        Query("orplan-id-two-residuals", () => rows.Query().Where(x => x.Id == 1234 &&
            ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99) &&
            ((x.Score > 0 && (x.Score < 19000 || x.Score == 19500)) || x.Score == 20000)));
        Query("orplan-city-linq", () => rows.Query().Where(x => x.City == "City234" &&
            ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99)));
        Query("orplan-equality-linq", () => rows.Query().Where(x => x.Score == 1234 || x.Score == 17890));
        var equality = BsonExpression.Create(string.Join(" OR ", Enumerable.Range(1, 64).Select(i => "Score = " + -i)));
        Query("orplan-equality-64-builder", () => rows.Query().Where(equality));
        Query("orplan-range-linq", () => rows.Query().Where(x =>
            (x.Score >= 1000 && x.Score < 1010) || (x.Score >= 15000 && x.Score < 15010)));
        var ranges = BsonExpression.Create(string.Join(" OR ", Enumerable.Range(1, 8).Select(i =>
            "(Score >= " + (-i * 10) + " AND Score < " + (-i * 10 + 2) + ")")));
        Query("orplan-range-8-builder", () => rows.Query().Where(ranges));
        Query("orplan-common-guard", () => rows.Query().Where(x =>
            (x.City == "City234" && x.Score < 2000) || (x.City == "City234" && x.Score > 18000)));
        Query("orplan-empty-with-id", () => rows.Query().Where(x => x.Id == 1234 &&
            ((x.Score > 1500 && x.Score < 1000) || (x.Score > 19000 && x.Score <= 19000))));
        measure("orplan-point-control", 4000, i => rows.FindById(1234).Id);
        plans["orplan-point-control"] = rows.Query().Where(x => x.Id == 1234).GetPlan().ToString();

        // Keep the exact step-46 operations and fixtures for the regression check.
        // The remaining INCLUDE workloads only contribute untimed setup/plans.
        var includePlans = new Dictionary<string, string>();
        IncludedBooleanWorkloads.Run(db, (name, iterations, operation) =>
        {
            if (name == "includebool-cheaper-id-control") measure("orplan-include-id", iterations, operation);
            if (name == "includebool-point-control") measure("orplan-include-point-control", iterations, operation);
        }, includePlans, "includebool");
        plans["orplan-include-point-control"] = includePlans["includebool-point"];
        plans["orplan-include-id"] = db.GetCollection<IncludedBooleanWorkloads.Row>("includedRows").Query()
            .Include(x => x.Ref).Where(x => x.Id == 1234 &&
                ((x.Score >= 1000 && (x.Score < 1500 || x.Score > 19000)) || x.Score == 99)).GetPlan().ToString();

        void Query(string name, Func<ILiteQueryable<Program.Row>> query)
        {
            plans[name] = query().GetPlan().ToString();
            measure(name, 2000, i => query().ToList().Sum(x => x.Id));
        }
    }
}
