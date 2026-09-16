using System;
using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class SharedQueryIr_Tests
    {
        [Fact]
        public void Sql_and_linq_have_identical_results_and_complete_execution_plans()
        {
            using var db = CreateDatabase();
            var people = db.GetCollection<Person>("people");
            var age = 30;
            var city = "Linz";
            AssertQuery(db, people.Query().Where(p => p.Age >= age && p.City == city)
                .OrderByDescending(p => p.Age).ThenBy(p => p.Id)
                .Select(p => new { p.Id, p.Name }).Offset(2).Limit(5),
                "SELECT {Id: $._id, Name: $.Name} FROM people WHERE (($.Age >= @p0) AND ($.City = @p1)) ORDER BY $.Age DESC, $._id ASC LIMIT 5 OFFSET 2",
                new BsonDocument { ["p0"] = age, ["p1"] = city });
            AssertQuery(db, people.Query().Where(p => p.Age >= age).OrderBy(p => p.Age)
                .Select(p => new { p.Age }).Limit(5),
                "SELECT {Age: $.Age} FROM people WHERE ($.Age >= @p0) ORDER BY $.Age LIMIT 5",
                new BsonDocument { ["p0"] = age });
            AssertQuery(db, people.Query().Where(p => p.Name.StartsWith("Person1")).Select(p => new { p.Id, p.Name }),
                "SELECT {Id: $._id, Name: $.Name} FROM people WHERE $.Name LIKE (@p0 + '%')",
                new BsonDocument { ["p0"] = "Person1" });
            AssertQuery(db, people.Query().GroupBy(p => p.City).OrderBy(g => g.Count())
                .Select(g => new { g.Key, Count = g.Count(), Ages = g.Select(p => p.Age).ToArray() }),
                "SELECT {Key: @key, Count: COUNT(*), Ages: ARRAY(MAP(* => @.Age))} FROM people GROUP BY $.City ORDER BY COUNT(*)",
                new BsonDocument());
        }

        [Fact]
        public void Expression_indexes_match_sql_and_linq_in_both_creation_directions()
        {
            foreach (var sqlIndex in new[] { true, false })
            {
                using var db = CreateDatabase();
                var people = db.GetCollection<Person>("people");
                if (sqlIndex) people.EnsureIndex("upper_name", " UPPER ( $.Name ) ");
                else people.EnsureIndex("upper_name", p => p.Name.ToUpper());
                var name = "PERSON12";
                var query = people.Query().Where(p => p.Name.ToUpper() == name);
                query.GetPlan()["index"]["name"].AsString.Should().Be("upper_name");
                AssertQuery(db, query, "SELECT $ FROM people WHERE (UPPER($.Name) = @p0)",
                    new BsonDocument { ["p0"] = name });
            }
        }

        [Fact]
        public void Rebinding_uses_live_index_metadata_and_does_not_store_a_physical_plan()
        {
            using var db = CreateDatabase();
            var people = db.GetCollection<Person>("people");
            var name = "Person12";
            var template = db.Mapper.GetExpression<Person, bool>(p => p.Name == name);
            var query = people.Query().Where(template.Bind(new BsonDocument { ["p0"] = "Person20" }));
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            people.EnsureIndex(p => p.Name);
            query.GetPlan()["index"]["name"].AsString.Should().Be("Name");
            query.ToArray().Select(p => p.Id).Should().Equal(20);
            people.DropIndex("Name");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(p => p.Id).Should().Equal(20);
        }

        [Fact]
        public void Contains_normalization_is_shared_and_does_not_parse_generated_text()
        {
            using var db = CreateDatabase();
            var people = db.GetCollection<Person>("people");
            var ids = new[] { 3, 7, 11 };
            BsonExpression normalized;
            var predicate = db.Mapper.GetExpression<Person, bool>(p => ids.Contains(p.Id));
            using (new DirectTranslationScope()) normalized = BsonExpressionFactory.NormalizeContains(predicate);
            normalized.Type.Should().Be(BsonExpressionType.In);
            normalized.ExecuteScalar(new BsonDocument { ["_id"] = 7 }).AsBoolean.Should().BeTrue();
            normalized.ExecuteScalar(new BsonDocument { ["_id"] = 8 }).AsBoolean.Should().BeFalse();
            AssertQuery(db, people.Query().Where(p => ids.Contains(p.Id)),
                "SELECT $ FROM people WHERE @p0 ANY = $._id", new BsonDocument { ["p0"] = new BsonArray(ids.Select(x => new BsonValue(x))) });
            people.Query().Where(predicate).GetPlan()["index"]["name"].AsString.Should().Be("_id");
        }

        private static void AssertQuery<T>(LiteDatabase db, ILiteQueryableResult<T> query, string sql, BsonDocument parameters)
        {
            using var reader = db.Execute(sql, parameters);
            query.ToDocuments().ToArray().Should().Equal(reader.ToEnumerable().Select(x => x.AsDocument));
            using var explain = db.Execute("EXPLAIN " + sql, parameters);
            var sqlPlan = explain.ToEnumerable().Single().AsDocument;
            ((BsonValue)query.GetPlan()).Should().Be(sqlPlan);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var people = db.GetCollection<Person>("people");
            people.InsertBulk(Enumerable.Range(1, 100).Select(i => new Person
            {
                Id = i, Age = 20 + i % 50, Name = "Person" + i, City = i % 3 == 0 ? "Linz" : "Vienna"
            }));
            people.EnsureIndex(p => p.Age);
            people.EnsureIndex(p => p.City);
            return db;
        }

        public class Person
        {
            public int Id { get; set; }
            public int Age { get; set; }
            public string Name { get; set; }
            public string City { get; set; }
        }
    }
}
