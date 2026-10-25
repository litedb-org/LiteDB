using System.Linq;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2929_Tests
    {
        private const string ReporterDigits = "1111111111111111111111111";
        private const string ReporterWarning = "index 'Name' holds String keys; the Decimal value cannot match";

        [Theory]
        [InlineData("11111")]
        [InlineData("1111111111111111111")]
        [InlineData(ReporterDigits)]
        public void Digits_only_string_field_is_found_by_parameter_query_api_and_quoted_literal(string digits)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, new BsonValue(digits), new BsonValue("other"));

            Assert.Equal(1, rows.Find(BsonExpression.Create("$.Name = @0", digits)).Single()["_id"].AsInt32);
            Assert.Equal(1, rows.Find(Query.EQ("Name", digits)).Single()["_id"].AsInt32);
            Assert.Equal(1, rows.Find("$.Name = '" + digits + "'").Single()["_id"].AsInt32);
            Assert.Equal(1, rows.Find("STRING($.Name) = '" + digits + "'").Single()["_id"].AsInt32);
            Assert.Empty(rows.Find("$.Name = " + digits));
        }

        [Fact]
        public void Documented_conversion_and_range_semantics_hold()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, new BsonValue("123"), new BsonValue(123));

            Assert.Equal(2, rows.Find("STRING($.Name) = '123'").Count());
            Assert.StartsWith("FULL INDEX SCAN", rows.Query().Where("STRING($.Name) = '123'").GetPlan()["index"]["mode"].AsString);

            rows.EnsureIndex("NameText", "STRING($.Name)");

            Assert.StartsWith("INDEX SEEK(NameText", rows.Query().Where("STRING($.Name) = '123'").GetPlan()["index"]["mode"].AsString);
            Assert.Equal(1, rows.Find("$.Name > 5").Count(x => x["Name"].IsString));
        }

        [Fact]
        public void Plan_warns_when_a_number_literal_seeks_an_index_of_strings()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, new BsonValue(ReporterDigits), new BsonValue("other"));

            var plan = rows.Query().Where("$.Name = " + ReporterDigits).GetPlan();
            var explained = db.Execute("EXPLAIN SELECT $ FROM rows WHERE $.Name = " + ReporterDigits).ToArray().Single();

            Assert.Equal(ReporterWarning, plan["index"]["warning"].AsString);
            Assert.Equal(ReporterWarning, explained["index"]["warning"].AsString);
        }

        [Fact]
        public void Plan_warns_when_a_string_value_seeks_an_index_of_numbers()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, new BsonValue(5), new BsonValue(7.5), new BsonValue(9L));

            Assert.Equal("index 'Name' holds numeric keys; the String value cannot match", Warning(rows, "$.Name = '5'"));
            Assert.Equal("index 'Name' holds numeric keys; the String value cannot match", Warning(rows, BsonExpression.Create("$.Name = @0", "5")));
        }

        [Fact]
        public void Plan_warns_for_IN_only_when_no_listed_value_can_match()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = CreateRows(db, new BsonValue("5"), new BsonValue("7"));

            Assert.Equal("index 'Name' holds String keys; the Int32, Double values cannot match", Warning(rows, "$.Name IN [5, 7.5, 9]"));
            Assert.Null(Warning(rows, "$.Name IN [5, '7']"));
        }

        [Fact]
        public void Plan_does_not_warn_when_the_value_can_match_or_the_index_proves_nothing()
        {
            using var db = new LiteDatabase(":memory:");
            var strings = CreateRows(db, new BsonValue("5"), new BsonValue("7"));
            var doubles = CreateRows(db.GetCollection("doubles"), new BsonValue(5.0), new BsonValue(7.5));
            var mixed = CreateRows(db.GetCollection("mixed"), new BsonValue(5), new BsonValue("7"));
            var nullable = CreateRows(db.GetCollection("nullable"), BsonValue.Null, new BsonValue("7"));
            var empty = CreateRows(db.GetCollection("empty"), new BsonValue("5"));
            empty.DeleteAll();

            Assert.Null(Warning(strings, "$.Name = '5'"));
            Assert.Null(Warning(doubles, "$.Name = 5"));
            Assert.Null(Warning(mixed, "$.Name = 5"));
            Assert.Null(Warning(mixed, "$.Name = true"));
            Assert.Null(Warning(nullable, "$.Name = 7"));
            Assert.Null(Warning(empty, "$.Name = 5"));
            Assert.Null(Warning(strings, "$.Other = 5"));
            Assert.Null(Warning(strings, "$.Name > 5"));
            Assert.Null(Warning(strings, "$.Name BETWEEN 1 AND 5"));
        }

        [Fact]
        public void Normal_execution_builds_its_plan_without_the_index_key_lookup()
        {
            var engine = new LiteEngine(new EngineSettings { Filename = ":memory:" });
            using var db = new LiteDatabase(engine);
            var rows = CreateRows(db, new BsonValue(ReporterDigits));
            var query = new Query();
            query.Where.Add(BsonExpression.Create("$.Name = " + ReporterDigits));
            var pragmas = new EnginePragmas(null);
            var monitor = engine.GetMonitor();
            var transaction = monitor.GetTransaction(true, true, out var isNew);

            try
            {
                var snapshot = transaction.CreateSnapshot(LockMode.Read, rows.Name, false);
                var plan = new QueryOptimization(snapshot, query, null, pragmas.Collation).ProcessQuery();
                var indexer = new IndexService(snapshot, pragmas.Collation, 1_000_000);

                Assert.False(plan.GetExecutionPlan()["index"].AsDocument.ContainsKey("warning"));
                Assert.Equal(ReporterWarning, IndexKeyTypeWarning.Find(plan.Index, snapshot.CollectionPage, indexer));
            }
            finally
            {
                if (isNew)
                {
                    monitor.ReleaseTransaction(transaction);
                }
            }

            Assert.Empty(rows.Find("$.Name = " + ReporterDigits));
        }

        private static ILiteCollection<BsonDocument> CreateRows(LiteDatabase db, params BsonValue[] names)
        {
            return CreateRows(db.GetCollection("rows"), names);
        }

        private static ILiteCollection<BsonDocument> CreateRows(ILiteCollection<BsonDocument> rows, params BsonValue[] names)
        {
            rows.Insert(names.Select((name, index) => new BsonDocument { ["_id"] = index + 1, ["Name"] = name }));
            rows.EnsureIndex("Name", "$.Name");

            return rows;
        }

        private static string Warning(ILiteCollection<BsonDocument> rows, BsonExpression predicate)
        {
            var index = rows.Query().Where(predicate).GetPlan()["index"].AsDocument;

            return index.TryGetValue("warning", out var warning) ? warning.AsString : null;
        }
    }
}
