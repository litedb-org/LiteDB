using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class BooleanRangePipeline_Tests
    {
        [Theory]
        [InlineData(false, "(Score >= 1 AND Score <= 20 AND (Score < 10 OR Score > 10)) OR Score = 50")]
        [InlineData(true, "(Score >= 1 AND Score <= 20 AND (Score < 10 OR Score > 10)) OR Score = 50")]
        [InlineData(false, "Score >= 1 AND Score <= 20")]
        [InlineData(true, "Score >= 1 AND Score <= 20")]
        public void Updating_keys_into_later_ranges_visits_each_document_once(bool unique, string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { 1, 8 }.Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i, ["Hits"] = 0 }));
            rows.EnsureIndex("score", "Score", unique);
            rows.UpdateMany("{ Score: Score + 10, Hits: Hits + 1 }", predicate).Should().Be(2);
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Score"].AsInt32).Should().Equal(11, 18);
            rows.FindAll().Select(x => x["Hits"].AsInt32).Should().Equal(1, 1);
        }

        [Fact]
        public void Updating_keys_into_later_point_seeks_preserves_duplicate_documents()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 12).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 2 == 0 ? 1 : 8, ["Hits"] = 0 }));
            rows.EnsureIndex("score", "Score");
            rows.UpdateMany("{ Score: IIF(Score = 1, 8, 3), Hits: Hits + 1 }", "Score = 1 OR Score = 8").Should().Be(12);
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Score"].AsInt32)
                .Should().Equal(Enumerable.Range(1, 12).Select(i => i % 2 == 0 ? 8 : 3));
            rows.FindAll().Select(x => x["Hits"].AsInt32).Should().OnlyContain(x => x == 1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Combined_boolean_ranges_preserve_aggregates_projection_and_grouping(bool empty)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 40).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 10 }));
            rows.EnsureIndex("score", "Score");
            var predicate = empty ? "(Score < 4 OR Score > 6) AND Score >= 4 AND Score <= 6" :
                "(Score >= 2 AND (Score < 4 OR Score > 6)) AND Score <= 8";
            var projection = rows.Query().Where(predicate).OrderBy("Score", Query.Descending).Select("Score");
            projection.GetPlan().ContainsKey("filters").Should().BeFalse();
            if (!empty) projection.GetPlan()["lookup"]["loader"].AsString.Should().Be("index");
            projection.ToArray().Length.Should().Be(empty ? 0 : 16);
            projection.Offset(5).Limit(3).Count().Should().Be(empty ? 0 : 3);
            var aggregate = rows.Query().Where(predicate).Select("{ n: COUNT(*), present: ANY(*) }");
            aggregate.GetPlan()["lookup"]["loader"].AsString.Should().Be("none");
            var result = aggregate.ToDocuments().Single();
            result["n"].AsInt32.Should().Be(empty ? 0 : 16);
            result["present"].AsBoolean.Should().Be(!empty);
            using var grouped = db.Execute("SELECT { key: @key, n: COUNT(*) } FROM rows WHERE " + predicate + " GROUP BY Score");
            var groups = grouped.ToEnumerable().ToArray();
            groups.Select(x => x["key"].AsInt32).Should().Equal(empty ? new int[0] : new[] { 2, 3, 7, 8 });
            groups.Select(x => x["n"].AsInt32).Should().Equal(Enumerable.Repeat(4, empty ? 0 : 4));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Nested_ranges_survive_page_release_rollback_and_reopen(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password, CacheSize = 65536, TransactionPageLimit = 1 };
            const string predicate = "(Score >= 10 AND Score <= 80 AND (Score < 20 OR Score > 70)) OR Score = 5";
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i % 100, ["Payload"] = new string('x', 800) }));
                rows.EnsureIndex("score", "Score");
                db.Checkpoint();
                db.BeginTrans();
                rows.DeleteMany(predicate).Should().Be(420);
                rows.Count(predicate).Should().Be(0);
                db.Rollback();
                rows.Count(predicate).Should().Be(420);
                db.BeginTrans();
                rows.UpdateMany("{ Score: Score + 61 }", predicate).Should().Be(420);
                rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Score"].AsInt32).Should().Equal(
                    Enumerable.Range(1, 2000).Select(i => i % 100).Select(score =>
                        ((score >= 10 && score < 20) || (score > 70 && score <= 80) || score == 5) ? score + 61 : score));
                db.Rollback();
            }
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                var query = rows.Query().Where(predicate).OrderBy("Score", Query.Descending);
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                var expected = Enumerable.Range(1, 2000).Select(i => i % 100).Where(i =>
                    (i >= 10 && i < 20) || (i > 70 && i <= 80) || i == 5).OrderByDescending(i => i).ToArray();
                query.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(expected);
                query.Offset(190).Limit(40).ToArray().Select(x => x["Score"].AsInt32).Should().Equal(expected.Skip(190).Take(40));
            }
        }

        [Fact]
        public void Existing_templates_use_rebuilt_collation_for_boolean_intersections()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                seed.GetCollection("rows").InsertBulk(new[] { "a", "A", "b", "B", "c" }.Select(value => new BsonDocument { ["Value"] = value }));
            }
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "Value");
            var template = BsonExpression.Create("(Value >= @low AND (Value < @high OR Value > @other)) OR Value = @point",
                new BsonDocument { ["low"] = "a", ["high"] = "b", ["other"] = "B", ["point"] = "c" });
            rows.Find(template).Select(x => x["Value"].AsString).Should().BeEquivalentTo(new[] { "a", "b", "c" });
            db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") });
            rows.Find(template).Select(x => x["Value"].AsString).Should().BeEquivalentTo(new[] { "a", "A", "c" });
        }
    }
}
