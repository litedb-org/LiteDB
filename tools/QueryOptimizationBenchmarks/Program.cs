using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDB;

internal static class Program
{
    private static long _sink;

    private static void Main(string[] args)
    {
        if (args.Length > 1 && args[1] == "textir-parallel")
        {
            ParsedExpressionParallel.Run(args[0]);
            return;
        }
        using var db = new LiteDatabase(":memory:");
        var rows = db.GetCollection<Row>("rows");
        rows.InsertBulk(Enumerable.Range(1, 20000).Select(i => new Row
        {
            Id = i, Score = i, City = "City" + i % 1000, Name = "Person" + i
        }));
        rows.EnsureIndex(x => x.Score);
        rows.EnsureIndex(x => x.City);
        var enabled = true;
        var results = new List<object>();
        var plans = new Dictionary<string, string>();
        if (args.Length > 1 && args[1].StartsWith("overall", StringComparison.Ordinal))
        {
            OverallWorkloads.Run(db, Measure, plans);
        }
        else
        {
            ConstraintWorkloads.Run(db, Measure, plans);
            BooleanWorkloads.Run(db, Measure, plans);
            DiagnosticsWorkloads.Run(db, Measure, plans);
            AggregateWorkloads.Run(db, Measure, plans);
            HelperWorkloads.Run(db, Measure, plans);
            MetadataWorkloads.Run(db, Measure, plans);
            ReplayWorkloads.Run(Measure, plans, args.Length > 1 ? args[1] : null);
            CommonWorkloads.Run(db, Measure, plans);
            CacheWorkloads.Run(db, Measure, plans);
            TopNWorkloads.Run(db, Measure, plans);
            PrimaryWorkloads.Run(db, Measure, plans);
            PlanningWorkloads.Run(db, Measure, plans);
            SourceWorkloads.Run(db, Measure, plans);
            BindingWorkloads.Run(db, Measure, plans);
            NestedWorkloads.Run(db, Measure, args.Length > 1 ? args[1] : null);
            UniqueWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            ScalarIndexWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            SqlCacheWorkloads.Run(db, Measure, args.Length > 1 ? args[1] : null);
            LikeCharacterWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            LikeTerminalWorkloads.Run(Measure, args.Length > 1 ? args[1] : null);
            BooleanValueWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            ParsedExpressionWorkloads.Run(db, Measure, args.Length > 1 ? args[1] : null);
            RangeUnionWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            SetUnionWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            BooleanRangeWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            BooleanMembershipWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            BooleanWriteWorkloads.Run(db, Measure, args.Length > 1 ? args[1] : null);
            FieldCaseWorkloads.Run(db, Measure, plans);
            NestedFieldCaseWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            IncludedBooleanWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            DisjunctionPlanningWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            DocumentKeyWorkloads.Run(db, Measure, args.Length > 1 ? args[1] : null);
            NodeLinkWorkloads.Run(db, Measure);
            MergeWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            TraversalGuardWorkloads.Run(db, Measure);
            CollationWorkloads.Run(db, Measure);
            ExclusiveRangeWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            ExclusionWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            EscapedFieldWorkloads.Run(db, Measure, plans, args.Length > 1 ? args[1] : null);
            Measure("or-linq", 20, i => rows.Query().Where(x => x.Score == 1234 || x.Score == 17890)
                .ToList().Sum(x => x.Id));
            Measure("or-sql", 20, i => Read("SELECT $ FROM rows WHERE Score = 1234 OR Score = 17890"));
            Measure("fold-linq", 20, i => rows.Query().Where(x => !enabled || x.Score == 1234).ToList().Sum(x => x.Id));
            Measure("fold-sql", 20, i =>
            {
                using var reader = db.Execute("SELECT $ FROM rows WHERE @enabled = false OR Score = @score",
                    new BsonDocument { ["enabled"] = enabled, ["score"] = 1234 });
                long sum = 0;
                while (reader.Read()) sum += reader.Current["_id"].AsInt32;
                return sum;
            });
            Measure("range-linq", 10, i => rows.Query().Where(x => x.Score >= 10000 && x.Score < 10010)
                .ToList().Sum(x => x.Id));
            Measure("range-sql", 10, i => Read("SELECT $ FROM rows WHERE Score >= 10000 AND Score < 10010"));
            Measure("contradiction-linq", 10, i => rows.Query().Where(x => x.Score > 10010 && x.Score < 10000)
                .ToList().Sum(x => x.Id));
            Measure("contradiction-unindexed", 10, i => rows.Query().Where(x => x.Name == "Person1" && x.Name == "Person2")
                .ToList().Sum(x => x.Id));
            Measure("ordinary-id", 1000, i =>
            {
                var id = i % 20000 + 1;
                return rows.Query().Where(x => x.Id == id).FirstOrDefault().Id;
            });
            Measure("ordinary-combined", 500, i =>
            {
                var city = "City" + i % 1000;
                var minimum = i % 500;
                return rows.Query().Where(x => x.City == city && x.Score >= minimum).FirstOrDefault()?.Id ?? 0;
            });
            Measure("ordinary-projection", 500, i =>
            {
                var city = "City" + i % 1000;
                return rows.Query().Where(x => x.City == city).Select(x => new { x.Id, x.Name })
                    .Limit(5).ToList().Sum(x => x.Id);
            });
            Measure("scan-control", 3, i => rows.Query().Where(x => x.Name.StartsWith("Person1"))
                .ToList().Sum(x => x.Id));

            plans["or"] = rows.Query().Where(x => x.Score == 1234 || x.Score == 17890).GetPlan().ToString();
            plans["range"] = rows.Query().Where(x => x.Score >= 10000 && x.Score < 10010).GetPlan().ToString();
            plans["fold"] = rows.Query().Where(x => !enabled || x.Score == 1234).GetPlan().ToString();
            plans["contradiction"] = rows.Query().Where(x => x.Name == "Person1" && x.Name == "Person2").GetPlan().ToString();
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            label = args.FirstOrDefault() ?? "current", runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, processors = Environment.ProcessorCount,
            assembly = typeof(LiteDatabase).Assembly.Location, rows = 20000, sink = _sink, plans, results
        }, new JsonSerializerOptions { WriteIndented = true }));

        long Read(string sql)
        {
            using var reader = db.Execute(sql);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        }

        void Measure(string name, int iterations, Func<int, long> operation)
        {
            if (args.Length > 1 && !name.StartsWith(args[1], StringComparison.Ordinal)) return;
            iterations = checked(iterations * (args.Length > 2 ? int.Parse(args[2]) : 1));
            var initial = _sink;
            for (var i = 0; i < iterations; i++) _sink += operation(i);
            var times = new double[9];
            var allocations = new double[9];
            var gen0 = new double[9];
            for (var sample = 0; sample < times.Length; sample++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var collections = GC.CollectionCount(0);
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < iterations; i++) _sink += operation(i);
                times[sample] = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / iterations;
                allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
                gen0[sample] = (GC.CollectionCount(0) - collections) * 1000.0 / iterations;
            }
            results.Add(new { name, iterations, checksum = _sink - initial, nanoseconds = times, bytes = allocations, gen0Per1000 = gen0 });
            Console.Error.WriteLine(name + ": " + times.OrderBy(t => t).ElementAt(4).ToString("F0") + " ns/op");
        }
    }

    public class Row
    {
        public int Id { get; set; }
        public int Score { get; set; }
        public string City { get; set; }
        public string Name { get; set; }
    }
}
