using System;
using System.Linq;

using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    /// <summary>
    /// LINQ shapes that real applications use on a generated collection: captured locals, string and
    /// collection methods, enum and date comparisons, lambda indexes, ordering, and paging.
    /// </summary>
    internal static class GeneratedLinqScenarios
    {
        internal static void Run(LiteDatabase database)
        {
            var people = database.GetGeneratedCollection<AotLinqRecord>("aot_linq");
            var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            people.Insert(new[]
            {
                new AotLinqRecord { Name = "Ada", Age = 36, State = AotNativeScalarState.Captured, CreatedAt = epoch.AddDays(1), Tags = ["math", "engine"] },
                new AotLinqRecord { Name = "Alan", Age = 41, State = AotNativeScalarState.Unknown, CreatedAt = epoch.AddDays(2), Tags = ["logic"] },
                new AotLinqRecord { Name = "Grace", Age = 85, State = AotNativeScalarState.Captured, CreatedAt = epoch.AddDays(3), Tags = ["compiler", "navy"] },
                new AotLinqRecord { Name = "Linus", Age = 19, State = AotNativeScalarState.Unknown, CreatedAt = epoch.AddDays(4), Tags = [] }
            });

            Console.WriteLine("  [3.16] Filter a generated collection with captured locals and logical operators.");
            var minimumAge = 30;
            var maximumAge = 50;
            var inRange = Names(people.Find(x => x.Age >= minimumAge && x.Age <= maximumAge));
            var outside = Names(people.Find(x => x.Age < minimumAge || x.Age > maximumAge));
            Report("age within captured range", inRange);
            Report("age outside captured range", outside);
            Require(inRange.SequenceEqual(new[] { "Ada", "Alan" }) && outside.SequenceEqual(new[] { "Grace", "Linus" }),
                "Captured-local predicates with logical operators failed.");

            Console.WriteLine("  [3.17] Translate string methods, enum comparisons, and date comparisons.");
            var prefix = "A";
            var state = AotNativeScalarState.Captured;
            var after = epoch.AddDays(2);
            Report("starts with captured prefix", Names(people.Find(x => x.Name.StartsWith(prefix))));
            Report("contains 'a'", Names(people.Find(x => x.Name.Contains("a"))));
            Report("upper-case equals", Names(people.Find(x => x.Name.ToUpper() == "GRACE")));
            Report("captured enum state", Names(people.Find(x => x.State == state)));
            Report("created after captured date", Names(people.Find(x => x.CreatedAt > after)));
            Require(Names(people.Find(x => x.State == state)).SequenceEqual(new[] { "Ada", "Grace" }) &&
                    Names(people.Find(x => x.CreatedAt > after)).SequenceEqual(new[] { "Grace", "Linus" }),
                "Enum or date predicates failed.");

            Console.WriteLine("  [3.18] Use captured collections and array members in predicates.");
            var ids = new[] { 1, 3 };
            var byIds = Names(people.Find(x => ids.Contains(x.Id)));
            var byTag = Names(people.Find(x => x.Tags.Contains("navy")));
            Report("id in captured array", byIds);
            Report("tag array contains", byTag);
            Require(byIds.SequenceEqual(new[] { "Ada", "Grace" }) && byTag.SequenceEqual(new[] { "Grace" }),
                "Captured-collection or array-member predicates failed.");

            Console.WriteLine("  [3.19] Create lambda indexes, then order, page, project, and aggregate.");
            Require(people.EnsureIndex(x => x.Age), "The lambda index was not created.");
            Require(people.EnsureIndex(x => x.Name, unique: true), "The unique lambda index was not created.");
            RequireThrows<LiteException>(
                () => people.Insert(new AotLinqRecord { Name = "Ada", Age = 1 }),
                "The unique lambda index accepted a duplicate.");

            var page = people.Query().OrderByDescending(x => x.Age).Select(x => x.Name).Skip(1).Limit(2).ToArray();
            Report("second page by age descending", page);
            Report("minimum age", people.Min(x => x.Age));
            Report("maximum created", people.Max(x => x.CreatedAt));
            Report("exists under 20", people.Exists(x => x.Age < 20));
            Report("long count over 40", people.LongCount(x => x.Age > 40));
            Require(page.SequenceEqual(new[] { "Alan", "Ada" }) && people.Min(x => x.Age) == 19,
                "Ordering, paging, projection, or aggregation failed.");

            Console.WriteLine("  [3.20] Reject every route from a generated collection into runtime model mapping.");
            var unmapped = new UnmappedCapturedValue { Threshold = 30 };
            var captured = new object[] { unmapped };
            Report("scalar member of a captured object", Names(people.Find(x => x.Age > unmapped.Threshold)));
            RequireThrows<NotSupportedException>(
                () => people.Find(x => captured.Contains(x.Name)).ToArray(),
                "A captured application object was serialized through runtime model mapping.");
            RequireThrows<NotSupportedException>(
                () => people.Include(x => x.Name),
                "Include did not reject the generated collection.");

            Console.WriteLine("        Passed: generated LINQ translation and fallback rejection.");
        }

        private static string[] Names(System.Collections.Generic.IEnumerable<AotLinqRecord> records) =>
            records.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }
}
