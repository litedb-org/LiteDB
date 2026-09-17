using System;
using System.Collections.Generic;
using System.Linq;

using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    /// <summary>
    /// Executes every registered BsonExpression method, function, and operator once. Published as Native AOT,
    /// every expression runs through the System.Linq.Expressions interpreter, which invokes these methods by
    /// reflection, so each one has to be proven individually. The scenario enumerates LiteDB's own method tables
    /// and fails when a registered method has no sweep entry, so a new method cannot be added without AOT coverage.
    /// </summary>
    internal static partial class ExpressionSweepScenarios
    {
        internal static void Run()
        {
            var document = CreateDocument();

            Console.WriteLine("  [5.1] Execute every registered expression method.");
            Sweep(document, Methods);
            RequireComplete("method", Methods, BsonExpression.Methods.Select(method =>
                Key(method.Name, method.GetParameters().Count(parameter => parameter.ParameterType != typeof(Collation)))));

            Console.WriteLine("  [5.2] Execute every registered expression function.");
            Sweep(document, Functions);
            RequireComplete("function", Functions, BsonExpression.Functions.Select(function =>
                Key(function.Name, function.GetParameters().Skip(5).Count())));

            Console.WriteLine("  [5.3] Execute every expression operator and path form.");
            Sweep(document, Operators);
            Report("operators covered", Operators.Select(entry => entry.Key).Distinct().Count());

            Console.WriteLine("        Passed: expression methods, functions, operators, and path forms.");
        }

        private static string Key(string name, int parameterCount) => name.ToUpperInvariant() + "~" + parameterCount;

        private static void Sweep(BsonDocument document, IEnumerable<(string Key, string Expression)> entries)
        {
            var parameters = new BsonDocument { ["0"] = 36, ["limit"] = 2 };

            var failures = new List<string>();

            foreach (var (key, expression) in entries)
            {
                BsonValue[] results;

                try
                {
                    results = BsonExpression.Create(expression, parameters).Execute(document).ToArray();
                }
                catch (Exception exception)
                {
                    // Keep going so one run names every broken entry, not just the first.
                    failures.Add($"{key} `{expression}`: {exception.GetType().Name}: {exception.Message}");
                    continue;
                }

                var rendered = results.Length == 1
                    ? JsonSerializer.Serialize(results[0])
                    : JsonSerializer.Serialize(new BsonArray(results));
                Console.WriteLine($"        = {key} `{expression}`: {rendered}");
            }

            Require(failures.Count == 0, "Expression sweep entries failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        private static void RequireComplete(string kind, IEnumerable<(string Key, string Expression)> entries, IEnumerable<string> registered)
        {
            var covered = new HashSet<string>(entries.Select(entry => entry.Key), StringComparer.Ordinal);
            var all = registered.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var missing = all.Where(key => covered.Contains(key) == false).ToArray();
            var unknown = covered.Where(key => all.Contains(key) == false).OrderBy(key => key, StringComparer.Ordinal).ToArray();

            Report($"registered {kind}s", all.Length);
            Require(missing.Length == 0, $"Registered expression {kind}s without a sweep entry: {string.Join(", ", missing)}.");
            Require(unknown.Length == 0, $"Sweep entries for unregistered expression {kind}s: {string.Join(", ", unknown)}.");
        }

        private static BsonDocument CreateDocument() => new BsonDocument
        {
            ["_id"] = 1,
            ["name"] = "  Ada Lovelace  ",
            ["age"] = 36,
            ["score"] = 12.5,
            ["big"] = 5000000000L,
            ["price"] = 19.99m,
            ["flag"] = true,
            ["nothing"] = BsonValue.Null,
            ["items"] = new BsonArray { 3, 1, 2, 3 },
            ["tags"] = new BsonArray { "alpha", "beta" },
            ["nested"] = new BsonDocument { ["x"] = 1, ["y"] = "two" },
            ["born"] = new DateTime(1815, 12, 10, 0, 0, 0, DateTimeKind.Utc),
            ["bin"] = new byte[] { 1, 2, 3 },
            ["guid"] = new Guid("d29368bb-0d4c-4b8c-9d52-3f6c2a8e1b11"),
            ["oid"] = new ObjectId("507f1f77bcf86cd799439011"),
            ["vec"] = new BsonVector(new[] { 1f, 0f })
        };
    }
}
