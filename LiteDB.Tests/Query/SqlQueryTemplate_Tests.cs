using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Mapper;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class SqlQueryTemplate_Tests
    {
        [Theory]
        [InlineData("SELECT $ FROM rows WHERE _id = @id")]
        [InlineData("SELECT { v: Score + @delta, name: Name } FROM rows WHERE Score >= @id ORDER BY Score DESC, Name ASC LIMIT 3 OFFSET 1")]
        [InlineData("SELECT { key: @key, n: COUNT(*), v: SUM(*.Score) + @delta } FROM rows WHERE Score >= @id GROUP BY City HAVING COUNT(*) > 0 ORDER BY @key DESC LIMIT 3")]
        [InlineData("SELECT { values: ARRAY(MAP([1,2] => @ + @delta)) }")]
        [InlineData("SELECT { values: ARRAY(FILTER([1,2,3] => @ > @delta)) }")]
        [InlineData("SELECT { values: ARRAY(SORT([3,2,1] => @ + @delta)) }")]
        [InlineData("SELECT { v: Values[@delta] } FROM rows WHERE _id = @id")]
        [InlineData("SELECT { v: ARRAY(Values[@ > @delta]) } FROM rows WHERE _id = @id")]
        [InlineData("SELECT { name: @name }")]
        [InlineData("SELECT { v: ARRAY(MAP(Values => @ + @delta)) } FROM rows WHERE _id = @id")]
        [InlineData("SELECT { n: COUNT(*) } FROM rows WHERE Score > @id")]
        [InlineData("SELECT $ FROM rows GROUP BY City")]
        [InlineData("EXPLAIN SELECT $ FROM rows WHERE Score >= @id AND Score < @id + 3")]
        [InlineData("SELECT $ FROM rows WHERE _id = @id; SELECT 999")]
        public void Reused_statements_match_fresh_parsing_with_current_parameters(string sql)
        {
            using var db = CreateDatabase();
            for (var i = 0; i < 5; i++)
            {
                var expected = Read(db, sql, Parameters(i), fresh: true);
                Read(db, sql, Parameters(i)).Should().Equal(expected);
            }
        }

        [Fact]
        public void Warm_string_queries_rebind_without_tokenizing()
        {
            using var db = CreateDatabase();
            const string sql = "SELECT { v: ARRAY(MAP(Values => @ + @delta)) } FROM rows WHERE _id = @id";
            Read(db, sql, Parameters(0));
            Read(db, sql, Parameters(0));
            using var scope = new DirectTranslationScope();
            Read(db, sql, Parameters(1)).Single()["v"].AsArray.ToArray().Should().Equal(new BsonValue[] { 2, 3, 4 });
        }

        [Fact]
        public void Root_group_projection_does_not_overwrite_predicate_parameters()
        {
            using var db = CreateDatabase();
            db.GetCollection("rows").EnsureIndex("cities", "City");
            const string sql = "SELECT $ FROM rows WHERE (_id + 0) > @key GROUP BY City";
            var expected = Read(db, sql, new BsonDocument { ["key"] = 0 }, fresh: true);
            for (var i = 0; i < 4; i++)
            {
                var parameters = new BsonDocument { ["key"] = 0 };
                Read(db, sql, parameters).Should().Equal(expected);
                parameters["key"].AsInt32.Should().Be(0);
            }
        }

        [Fact]
        public void Repeated_statements_use_live_index_metadata()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            const string sql = "EXPLAIN SELECT $ FROM rows WHERE Name = @name";
            string Index() => Read(db, sql, Parameters(0)).Single()["index"]["name"].AsString;
            Index().Should().Be("_id");
            Index().Should().Be("_id");
            rows.EnsureIndex("names", "Name");
            Index().Should().Be("names");
            rows.DropIndex("names");
            Index().Should().Be("_id");
        }

        [Fact]
        public void Binding_preserves_exact_sort_requirement()
        {
            var expression = BsonExpression.Create("Score % @divisor");
            expression.RequiresExactSort = true;

            var bound = expression.Bind(new BsonDocument { ["divisor"] = 3 });

            bound.RequiresExactSort.Should().BeTrue();
        }

        [Fact]
        public void Concurrent_and_interleaved_readers_have_independent_bindings()
        {
            using var db = CreateDatabase();
            const string sql = "SELECT { id: _id, v: Score + @delta } FROM rows WHERE _id >= @id ORDER BY _id LIMIT 2";
            Read(db, sql, Parameters(0));
            Read(db, sql, Parameters(0));
            Parallel.For(0, 100, i =>
            {
                var n = i % 5;
                Read(db, sql, Parameters(n)).Select(x => x["v"].AsInt32).Should().Equal(n * 2 + 1, n * 2 + 2);
            });
            using var first = db.Execute(sql, Parameters(0));
            using var second = db.Execute(sql, Parameters(3));
            first.Read().Should().BeTrue();
            second.Read().Should().BeTrue();
            first["v"].AsInt32.Should().Be(1);
            second["v"].AsInt32.Should().Be(7);
            first.Read().Should().BeTrue();
            first["v"].AsInt32.Should().Be(2);
            second.Read().Should().BeTrue();
            second["v"].AsInt32.Should().Be(8);
        }

        [Fact]
        public void Volatile_expressions_and_mutable_results_are_executed_anew()
        {
            using var db = CreateDatabase();
            const string sql = "SELECT { id: GUID(), nested: { values: [1,2] } }";
            Read(db, sql);
            var first = Read(db, sql).Single();
            first["nested"]["values"].AsArray.Add(99);
            var next = Read(db, sql).Single();
            next["id"].Should().NotBe(first["id"]);
            next["nested"]["values"].AsArray.ToArray().Should().Equal(new BsonValue[] { 1, 2 });
            Read(db, "SELECT 'UPPER'").Single()["expr"].AsString.Should().Be("UPPER");
            Read(db, "SELECT 'upper'").Single()["expr"].AsString.Should().Be("upper");
        }

        [Fact]
        public void Cached_select_into_and_for_update_repeat_their_work_and_honor_rollback()
        {
            using var db = CreateDatabase();
            const string sql = "SELECT $ INTO copies:int FROM rows WHERE _id = @id FOR UPDATE";
            for (var i = 0; i < 3; i++)
            {
                Read(db, "BEGIN");
                Read(db, sql, Parameters(i));
                db.GetCollection("copies").Count().Should().Be(1);
                Read(db, "ROLLBACK");
                db.GetCollection("copies").Count().Should().Be(0);
            }
            Read(db, sql, Parameters(3));
            db.GetCollection("copies").FindAll().Single()["_id"].AsInt32.Should().Be(4);
        }

        [Fact]
        public void Includes_and_system_collections_observe_current_data()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var related = db.GetCollection("related");
            related.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "first" });
            var row = rows.FindById(1);
            row["Ref"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "related" };
            rows.Update(row);
            const string include = "SELECT Ref FROM rows INCLUDE Ref WHERE _id = 1";
            Read(db, include);
            Read(db, include).Single()["Ref"]["value"].AsString.Should().Be("first");
            related.Update(new BsonDocument { ["_id"] = 1, ["value"] = "second" });
            Read(db, include).Single()["Ref"]["value"].AsString.Should().Be("second");
            const string system = "SELECT $ FROM $cols";
            Read(db, system);
            var count = Read(db, system).Length;
            db.GetCollection("added").Insert(new BsonDocument());
            Read(db, system).Length.Should().Be(count + 1);
        }

        [Fact]
        public void Reused_statements_keep_errors_and_recover_with_valid_parameters()
        {
            using var db = CreateDatabase();
            const string sql = "SELECT { v: SUBSTRING(Name, @start) } FROM rows WHERE _id = 1";
            Read(db, sql, new BsonDocument { ["start"] = 0 }).Single()["v"].AsString.Should().Be("Name1");
            Read(db, sql, new BsonDocument { ["start"] = 0 });
            using var scope = new DirectTranslationScope();
            Action invalid = () => Read(db, sql, new BsonDocument { ["start"] = 99 });
            invalid.Should().Throw<ArgumentOutOfRangeException>();
            Read(db, sql, new BsonDocument { ["start"] = 4 }).Single()["v"].AsString.Should().Be("1");
        }

        [Fact]
        public void Cached_statements_use_the_collation_after_rebuild()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString
            {
                Filename = file.Filename, Collation = new Collation("en-US/IgnoreCase")
            }))
            {
                seed.GetCollection("rows").Insert(new BsonDocument { ["Name"] = "lower" });
            }
            // Leave engine settings free to adopt the rebuilt header's collation.
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("names", "Name");
            const string scalar = "SELECT { same: 'lower' = 'LOWER' }";
            const string indexed = "SELECT $ FROM rows WHERE Name = 'LOWER'";
            Read(db, scalar).Single()["same"].AsBoolean.Should().BeTrue();
            Read(db, indexed).Should().HaveCount(1);
            Read(db, scalar);
            Read(db, indexed);
            db.Rebuild(new RebuildOptions { Collation = Collation.Binary });
            Read(db, scalar).Single()["same"].AsBoolean.Should().BeFalse();
            Read(db, indexed).Should().BeEmpty();
        }

        internal static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 20).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Score"] = i, ["City"] = "City" + i % 3,
                ["Name"] = "Name" + i, ["Values"] = new BsonArray(1, 2, 3)
            }));
            rows.EnsureIndex("scores", "Score");
            return db;
        }

        private static BsonDocument Parameters(int i) => new BsonDocument
        {
            ["id"] = i + 1, ["delta"] = i, ["name"] = "Name" + (i + 1)
        };

        internal static BsonValue[] Read(LiteDatabase db, string sql, BsonDocument parameters = null, bool fresh = false)
        {
            using var reader = fresh ? db.Execute(new StringReader(sql), parameters) : db.Execute(sql, parameters);
            var result = new List<BsonValue>();
            while (reader.Read()) result.Add(reader.Current);
            return result.ToArray();
        }
    }
}
