using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using LiteDB;

internal static class CacheWorkloads
{
    internal static void Run(LiteDatabase db, Action<string, int, Func<int, long>> measure, Dictionary<string, string> plans)
    {
        var rows = db.GetCollection<Program.Row>("rows");
        var shapes = new List<Expression<Func<Program.Row, bool>>>();
        foreach (var type in new[] { typeof(int), typeof(long), typeof(double), typeof(decimal) })
        {
            var root = Expression.Parameter(typeof(Program.Row), "x");
            Expression field = Expression.Property(root, nameof(Program.Row.Id));
            if (type != typeof(int)) field = Expression.Convert(field, type);
            Expression predicate = Expression.Equal(field, Expression.Constant(Convert.ChangeType(1234, type), type));
            for (var depth = 0; depth < 32; depth++)
            {
                shapes.Add(Expression.Lambda<Func<Program.Row, bool>>(predicate, root));
                predicate = Expression.Equal(predicate, Expression.Constant(true));
            }
        }
        // Fixed workload across processes and revisions: 128 repeated generated
        // predicate shapes, all selecting the same indexed row, with no explicit Bind.
        measure("cache-many-shapes", shapes.Count * 8, i => rows.Query().Where(shapes[i % shapes.Count]).FirstOrDefault().Id);
        plans["cache-entries"] = typeof(BsonMapper).GetProperty("LinqExpressionCacheCount", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(db.Mapper).ToString();
        measure("cache-single-shape-control", 4000, i => rows.Query().Where(x => x.Id == 1234).FirstOrDefault().Id);
        measure("cache-combined-control", 4000, i => rows.Query().Where(x => x.City == "City234" && x.Score >= 1000).FirstOrDefault().Id);
        plans["cache"] = rows.Query().Where(shapes[shapes.Count - 1]).GetPlan().ToString();
    }
}
