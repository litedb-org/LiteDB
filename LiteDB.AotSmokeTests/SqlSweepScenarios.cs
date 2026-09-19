using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using static LiteDB.AotSmokeTests.SmokeAssert;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    /// <summary>
    /// Runs every SQL statement kind and every SELECT clause through <see cref="LiteDatabase.Execute(string, BsonValue[])"/>
    /// and writes each result into the transcript, including the system collections.
    /// </summary>
    internal static class SqlSweepScenarios
    {
        internal static void Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"litedb-aot-sql-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            try
            {
                using var database = new LiteDatabase(Path.Combine(directory, "sql.db"));
                var exportPath = Path.Combine(directory, "export.json").Replace('\\', '/');

                Console.WriteLine("  [6.1] Insert, index, and query with every SELECT clause.");
                Sweep(database, DataStatements);

                Console.WriteLine("  [6.2] Update, delete, transactions, pragmas, and maintenance statements.");
                Sweep(database, MutationStatements);

                Console.WriteLine("  [6.3] Read the system collections and round-trip a JSON file.");
                Sweep(database, SystemStatements);
                Execute(database, "export to $file", $"SELECT $ INTO $file('{exportPath}') FROM people ORDER BY _id");
                Execute(database, "import from $file", $"SELECT name FROM $file('{exportPath}') ORDER BY name");

                Console.WriteLine("  [6.4] Rename and drop.");
                Sweep(database, SchemaStatements);

                Console.WriteLine("        Passed: every SQL statement kind, SELECT clause, and system collection.");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static readonly string[] DataStatements =
        {
            "INSERT INTO people:INT VALUES { name: 'Ada', age: 36, city: 'London', tags: ['math', 'engine'] }, { name: 'Alan', age: 41, city: 'London', tags: ['logic'] }, { name: 'Grace', age: 85, city: 'New York', tags: [] }, { name: 'Linus', age: 19, city: 'Helsinki', tags: ['kernel'] }",
            "INSERT INTO orders VALUES { _id: 1, total: 12.5, customer: { $id: 1, $ref: 'people' } }, { _id: 2, total: 99, customer: { $id: 3, $ref: 'people' } }",
            "CREATE INDEX idx_age ON people(age)",
            "CREATE UNIQUE INDEX idx_name ON people(LOWER(name))",
            "SELECT $ FROM people WHERE name LIKE 'A%' ORDER BY name",
            "SELECT name, age + 1 AS next FROM people WHERE age > 30 ORDER BY age DESC LIMIT 2 OFFSET 1",
            "SELECT { upper: UPPER(name), tagCount: COUNT(tags[*]) } FROM people WHERE tags[*] ANY = 'logic' OR age < 20 ORDER BY name",
            "SELECT COUNT(*) FROM people",
            "SELECT { total: COUNT(*), oldest: MAX(*.age), average: AVG(*.age) } FROM people",
            "SELECT { city: @key, people: COUNT(*), names: ARRAY(*.name) } FROM people GROUP BY city HAVING COUNT(*) > 1",
            "SELECT { id: _id, customer: customer.name, total } FROM orders INCLUDE customer ORDER BY _id",
            "SELECT name FROM people WHERE age BETWEEN @0 AND @1 ORDER BY name",
            "SELECT $ INTO seniors FROM people WHERE age > 40",
            "SELECT name FROM seniors ORDER BY name"
        };

        private static readonly string[] MutationStatements =
        {
            "UPDATE people SET age = age + 1, city = UPPER(city) WHERE name = 'Ada'",
            "SELECT { name, age, city } FROM people WHERE name = 'Ada'",
            "DELETE people WHERE age < 20",
            "BEGIN",
            "INSERT INTO people:INT VALUES { name: 'Rolled back', age: 1 }",
            "ROLLBACK",
            "BEGIN TRANS",
            "INSERT INTO people:INT VALUES { name: 'Committed', age: 2, city: 'Nowhere', tags: [] }",
            "COMMIT",
            "SELECT name FROM people ORDER BY name",
            "PRAGMA USER_VERSION = 7",
            "PRAGMA USER_VERSION",
            "CHECKPOINT",
            "REBUILD",
            "SELECT COUNT(*) FROM people"
        };

        private static readonly string[] SystemStatements =
        {
            "SELECT name FROM $cols ORDER BY name",
            "SELECT { collection, name, expression, unique } FROM $indexes WHERE collection = 'people' ORDER BY name",
            "SELECT COUNT(*) >= 0 FROM $sequences",
            "SELECT { encrypted, readOnly, userVersion: pragmas.USER_VERSION, collation: pragmas.COLLATION, hasEngine: IS_STRING(engine) } FROM $database",
            "SELECT COUNT(*) > 0 FROM $dump",
            "SELECT COUNT(*) > 0 FROM $page_list",
            "SELECT COUNT(*) >= 0 FROM $snapshots",
            "SELECT COUNT(*) >= 0 FROM $transactions",
            "SELECT COUNT(*) >= 0 FROM $open_cursors",
            "SELECT name FROM $query('SELECT name FROM people WHERE age > 80')"
        };

        private static readonly string[] SchemaStatements =
        {
            "RENAME COLLECTION seniors TO archive",
            "SELECT COUNT(*) FROM archive",
            "DROP INDEX people.idx_age",
            "DROP COLLECTION archive",
            "SELECT name FROM $cols ORDER BY name"
        };

        private static void Sweep(LiteDatabase database, IEnumerable<string> statements)
        {
            var failures = new List<string>();

            foreach (var statement in statements)
            {
                try
                {
                    Execute(database, statement, statement);
                }
                catch (Exception exception)
                {
                    // Keep going so one run names every broken statement, not just the first.
                    failures.Add($"`{statement}`: {exception.GetType().Name}: {exception.Message}");
                }
            }

            Require(failures.Count == 0, "SQL sweep statements failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }

        private static void Execute(LiteDatabase database, string label, string statement)
        {
            using var reader = database.Execute(statement, 30, 50);
            var results = new BsonArray(reader.ToEnumerable().ToArray());

            Console.WriteLine($"        = `{label}`: {JsonSerializer.Serialize(results)}");
        }
    }
}
