using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text.Json;
using LiteDB;

// Runs against either checkout's production assembly. Every execution workload
// consumes results; setup, index construction and warmup are outside measurements.
internal static class Program
{
    private static long _sink;

    private static void Main(string[] args)
    {
        using var db = new LiteDatabase(":memory:");
        var mapper = db.Mapper;
        var people = db.GetCollection<Person>("people");
        people.InsertBulk(Enumerable.Range(1, 10000).Select(i => new Person
        {
            Id = i, Age = i % 80, Name = "Person" + i, City = "City" + i % 100,
            CreatedOn = new DateTime(2020 + i % 5, 1, 1),
            Address = new Address { City = "City" + i % 100 }
        }));
        people.EnsureIndex(p => p.Age);
        people.EnsureIndex(p => p.City);
        var id = 1234;
        var city = "City34";
        var age = 30;
        var ids = new[] { 2, 4, 8 };
        var prefix = "Person1";
        var year = 2021;
        Expression<Func<Person, bool>> point = p => p.Id == id;
        Expression<Func<Person, bool>> nested = p => p.Address.City == city && p.Age >= age;
        Expression<Func<Person, bool>> contains = p => ids.Contains(p.Id);
        Expression<Func<Person, bool>> methods = p => p.Name.StartsWith(prefix) && p.CreatedOn.Year == year;
        Expression<Func<Person, object>> projection = p => new { p.Id, p.Name, p.Address.City };
        var results = new List<object>();

        Measure("translate-point", 5000, i => mapper.GetExpression(point).Parameters.Count);
        Measure("translate-nested", 5000, i => mapper.GetExpression(nested).Parameters.Count);
        Measure("translate-contains", 5000, i => mapper.GetExpression(contains).Parameters.Count);
        Measure("translate-methods", 5000, i => mapper.GetExpression(methods).Parameters.Count);
        Measure("translate-projection", 5000, i => mapper.GetExpression(projection).Fields.Count);
        Measure("construct-query", 3000, i =>
        {
            var query = people.Query().Where(p => p.Age >= age && p.City == city)
                .OrderBy(p => p.Name).Limit(10);
            GC.KeepAlive(query);
            return 1;
        });
        Measure("e2e-id", 2000, i =>
        {
            var value = i % 10000 + 1;
            return people.Query().Where(p => p.Id == value).FirstOrDefault().Id;
        });
        Measure("e2e-index-equality", 1000, i =>
        {
            var value = "City" + i % 100;
            return people.Query().Where(p => p.City == value).Limit(5).ToList().Sum(p => p.Id);
        });
        Measure("e2e-index-range", 1000, i =>
        {
            var value = i % 70;
            return people.Query().Where(p => p.Age >= value).OrderBy(p => p.Age).Limit(10)
                .ToList().Sum(p => p.Id);
        });
        Measure("e2e-index-projection", 1000, i =>
        {
            var value = "City" + i % 100;
            return people.Query().Where(p => p.City == value)
                .Select(p => new { p.Id, p.Name }).Limit(5).ToList().Sum(p => p.Id);
        });
        Measure("e2e-repeated-parameters", 1000, i =>
        {
            var value = "City" + i % 100;
            var minimum = i % 40;
            return people.Query().Where(p => p.Age >= minimum && p.City == value).FirstOrDefault()?.Id ?? 0;
        });
        Measure("e2e-full-scan", 10, i => people.Query().Where(p => p.Name.StartsWith(prefix))
            .ToList().Sum(p => p.Id));
        Measure("e2e-sql-id", 2000, i =>
        {
            using var reader = db.Execute("SELECT $ FROM people WHERE _id = @0", i % 10000 + 1);
            long sum = 0;
            while (reader.Read()) sum += reader.Current["_id"].AsInt32;
            return sum;
        });

        // Reflection is used once, outside measurement, so this same harness also
        // compiles against the baseline assembly which has no Bind API.
        var bindMethod = typeof(BsonExpression).GetMethod("Bind");
        if (bindMethod != null)
        {
            var bind = (Func<BsonExpression, BsonDocument, BsonExpression>)bindMethod.CreateDelegate(
                typeof(Func<BsonExpression, BsonDocument, BsonExpression>));
            var template = mapper.GetExpression(point);
            Measure("bind-point", 5000, i => bind(template, new BsonDocument { ["p0"] = i }).Parameters.Count);
            Measure("e2e-bound-id", 2000, i => people.Query()
                .Where(bind(template, new BsonDocument { ["p0"] = i % 10000 + 1 })).FirstOrDefault().Id);
            var repeated = mapper.GetExpression<Person, bool>(p => p.Age >= age && p.City == city);
            Measure("e2e-bound-repeated-parameters", 1000, i => people.Query()
                .Where(bind(repeated, new BsonDocument { ["p0"] = i % 40, ["p1"] = "City" + i % 100 }))
                .FirstOrDefault()?.Id ?? 0);
        }

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            label = args.FirstOrDefault() ?? "current", runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription, processors = Environment.ProcessorCount,
            assembly = typeof(LiteDatabase).Assembly.Location, sink = _sink, results
        }, new JsonSerializerOptions { WriteIndented = true }));

        void Measure(string name, int iterations, Func<int, long> operation)
        {
            var initial = _sink;
            for (var i = 0; i < iterations; i++) _sink += operation(i);
            var times = new double[15];
            var allocations = new double[15];
            var gen0 = new double[15];
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
            Console.Error.WriteLine(name + ": " + times.OrderBy(t => t).ElementAt(7).ToString("F0") + " ns/op");
        }
    }

    public class Person
    {
        public int Id { get; set; }
        public int Age { get; set; }
        public string Name { get; set; }
        public string City { get; set; }
        public DateTime CreatedOn { get; set; }
        public Address Address { get; set; }
    }

    public class Address
    {
        public string City { get; set; }
    }
}
