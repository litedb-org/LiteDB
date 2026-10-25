using System;
using System.Collections.Generic;
using LiteDB;

internal static class BindingWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var provider = new Provider();
        measure("binding-method-id", 2000, i =>
        {
            provider.Value = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == provider.Get()).FirstOrDefault().Id;
        });
        measure("binding-member-method-id", 2000, i =>
        {
            provider.Value = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == provider.Self().Value).FirstOrDefault().Id;
        });
        measure("binding-two-methods", 2000, i =>
        {
            provider.Value = i % 19000 + 1;
            return rows.Query().Where(x => x.Score >= provider.Get() && x.Score < provider.Add(10)).Count();
        });
        measure("binding-field-control", 4000, i =>
        {
            var value = i % 20000 + 1;
            return rows.Query().Where(x => x.Id == value).FirstOrDefault().Id;
        });
        measure("binding-new-mapper-control", 1000, i =>
        {
            provider.Value = i % 20000 + 1;
            var expression = new BsonMapper().GetExpression<Program.Row, bool>(x => x.Id == provider.Get());
            return rows.Query().Where(expression).FirstOrDefault().Id;
        });
        plans["binding"] = rows.Query().Where(x => x.Id == provider.Get()).GetPlan().ToString();
    }

    private class Provider
    {
        public int Value { get; set; }
        public int Get() => Value;
        public int Add(int amount) => Value + amount;
        public Provider Self() => this;
    }
}
