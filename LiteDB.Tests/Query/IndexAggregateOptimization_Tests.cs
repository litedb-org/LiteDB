using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class IndexAggregateOptimization_Tests
    {
        [Theory]
        [InlineData("{ n: COUNT(*), present: ANY(*) }")]
        [InlineData("{ n: COUNT(*._id), present: ANY(*._id) }")]
        [InlineData("{ n: COUNT(*.Missing), present: ANY(*.Missing) }")]
        [InlineData("{ n: COUNT(*.Missing.Nested), present: ANY(*.Missing.Nested) }")]
        public void Pure_row_aggregates_need_no_document_lookup(string select)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where("Score >= 3 AND Score <= 7").Select(select);
            query.GetPlan()["pipe"].AsString.Should().Be("indexAggregatePipe");
            query.GetPlan()["lookup"]["loader"].AsString.Should().Be("none");
            var result = query.ToDocuments().Single();
            result["n"].AsInt32.Should().Be(5);
            result["present"].AsBoolean.Should().BeTrue();
        }

        [Theory]
        [InlineData(0, 100, 10)]
        [InlineData(2, 3, 3)]
        [InlineData(8, 5, 2)]
        [InlineData(10, 3, 0)]
        [InlineData(0, 0, 0)]
        public void Ordinary_count_longcount_and_exists_preserve_pagination(int offset, int limit, int expected)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection<RangeOptimization_Tests.Row>("rows").Query().Where(x => x.Score >= 1)
                .OrderByDescending(x => x.Score).Offset(offset).Limit(limit);
            query.Count().Should().Be(expected);
            query.LongCount().Should().Be(expected);
            query.Exists().Should().Be(expected > 0);
            query.ToArray().Length.Should().Be(expected); // Aggregate helpers restore the original select.
        }

        [Fact]
        public void Multikey_index_entries_count_each_document_once()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 3, 5) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            rows.EnsureIndex("values", "Values[*]");
            var query = rows.Query().Where("Values[*] ANY >= 1").Select("{ n: COUNT(*), present: ANY(*) }");
            query.GetPlan()["pipe"].AsString.Should().Be("indexAggregatePipe");
            query.ToDocuments().Single()["n"].AsInt32.Should().Be(2);
            rows.Query().Where("Values[*] ANY IN [1,2,3]").Count().Should().Be(2);
        }

        [Theory]
        [InlineData("Score > 30")]
        [InlineData("Score > 5 AND Score < 2")]
        [InlineData("false")]
        public void Empty_index_results_preserve_aggregate_values(string predicate)
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Where(predicate).Select("{ n: COUNT(*), present: ANY(*) }");
            query.GetPlan()["pipe"].AsString.Should().Be("indexAggregatePipe");
            var result = query.ToDocuments().Single();
            result["n"].AsInt32.Should().Be(0);
            result["present"].AsBoolean.Should().BeFalse();
        }

        [Fact]
        public void Parameters_and_live_index_changes_are_replanned()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var template = BsonExpression.Create("Score >= @min", new BsonDocument { ["min"] = 5 });
            rows.Query().Where(template).Count().Should().Be(6);
            rows.Query().Where(template.Bind(new BsonDocument { ["min"] = 8 })).Count().Should().Be(3);
            rows.DropIndex("Score");
            var query = rows.Query().Where(template).Select("{ n: COUNT(*) }");
            query.GetPlan()["pipe"].AsString.Should().Be("queryPipe");
            query.ToDocuments().Single()["n"].AsInt32.Should().Be(6);
            rows.EnsureIndex("Score", "Score");
            query.GetPlan()["pipe"].AsString.Should().Be("indexAggregatePipe");
        }

        [Theory]
        [InlineData("{ n: COUNT(*.Values[*]) }")]
        [InlineData("{ n: COUNT(*), total: SUM(*.Score) }")]
        [InlineData("{ n: COUNT(MAP(* => RANDOM())) }")]
        [InlineData("{ n: COUNT(MAP(* => SUBSTRING(@.Name, 1000))) }")]
        public void Other_aggregate_expressions_use_the_existing_pipeline(string select)
        {
            using var db = CreateDatabase();
            db.GetCollection("rows").Query().Select(select).GetPlan()["pipe"].AsString.Should().Be("queryPipe");
        }

        [Fact]
        public void Residual_filters_sorting_includes_and_grouping_keep_their_pipeline()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var residual = rows.Query().Where("Score >= 3 AND Name = 'row'").Select("{ n: COUNT(*) }");
            residual.GetPlan()["pipe"].AsString.Should().Be("queryPipe");
            residual.ToDocuments().Single()["n"].AsInt32.Should().Be(8);
            var sorted = rows.Query().OrderBy("Name").Select("{ n: COUNT(*) }");
            sorted.GetPlan()["pipe"].AsString.Should().Be("queryPipe");
            sorted.ToDocuments().Single()["n"].AsInt32.Should().Be(10);
            rows.Query().Include("Owner").Select("{ n: COUNT(*) }").GetPlan()["pipe"].AsString.Should().Be("queryPipe");
            using var grouped = db.Execute("SELECT { key: @key, n: COUNT(*) } FROM rows GROUP BY Name");
            grouped.ToEnumerable().Single()["n"].AsInt32.Should().Be(10);
        }

        [Fact]
        public void Counts_observe_transaction_changes_and_rollback()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            db.BeginTrans();
            rows.Insert(new BsonDocument { ["_id"] = 11, ["Score"] = 11 });
            rows.Query().Where("Score >= 1").Count().Should().Be(11);
            rows.Query().Where("Score = 11").Exists().Should().BeTrue();
            db.Rollback();
            rows.Query().Where("Score >= 1").Count().Should().Be(10);
            rows.Query().Where("Score = 11").Exists().Should().BeFalse();
            db.GetCollection("missing").Query().Count().Should().Be(0);
            db.GetCollection("missing").Query().Exists().Should().BeFalse();
        }

        [Fact]
        public void Update_queries_preserve_the_document_pipeline()
        {
            using var db = CreateDatabase();
            var query = db.GetCollection("rows").Query().Select("{ n: COUNT(*) }").ForUpdate();
            query.GetPlan()["pipe"].AsString.Should().Be("queryPipe");
            query.ToDocuments().Single()["n"].AsInt32.Should().Be(10);
        }

        [Fact]
        public void Sql_count_and_any_aliases_and_scalar_aggregate_forms_are_preserved()
        {
            using var db = CreateDatabase();
            using var reader = db.Execute("SELECT COUNT(*) AS total, ANY(*._id) AS found FROM rows WHERE Score IN [3,5,7]");
            var result = reader.ToEnumerable().Single();
            result["total"].AsInt32.Should().Be(3);
            result["found"].AsBoolean.Should().BeTrue();
            var query = db.GetCollection("rows").Query().Select("COUNT(*)");
            query.GetPlan()["pipe"].AsString.Should().Be("indexAggregatePipe");
            query.ToDocuments().Single().Values.Single().AsInt32.Should().Be(10);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i, ["Name"] = "row" }));
            rows.EnsureIndex("Score", "Score");
            return db;
        }
    }
}
