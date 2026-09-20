using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class NestedIndexPipeline_Tests
    {
        [Theory]
        [InlineData(false, "owner.score >= 1 AND OWNER.SCORE <= 20")]
        [InlineData(true, "owner.score >= 1 AND OWNER.SCORE <= 20")]
        [InlineData(false, "(owner.score >= 1 AND owner.score <= 20 AND (owner.score < 10 OR OWNER.SCORE > 10)) OR owner.score = 50")]
        [InlineData(true, "(owner.score >= 1 AND owner.score <= 20 AND (owner.score < 10 OR OWNER.SCORE > 10)) OR owner.score = 50")]
        public void Key_moving_updates_visit_each_document_once(bool unique, string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(new[] { 1, 8 }.Select(i => new BsonDocument { ["_id"] = i, ["Owner"] = new BsonDocument { ["Score"] = i }, ["Hits"] = 0 }));
            rows.EnsureIndex("nested", "Owner.Score", unique);
            rows.Query().Where(predicate).GetPlan()["index"]["name"].AsString.Should().Be("nested");
            rows.UpdateMany("{ Owner: { Score: owner.score + 10 }, Hits: Hits + 1 }", predicate).Should().Be(2);
            rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Owner"]["Score"].AsInt32).Should().Equal(11, 18);
            rows.FindAll().Select(x => x["Hits"].AsInt32).Should().Equal(1, 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void Nested_member_indexes_survive_page_release_rollback_and_reopen(string password)
        {
            using var file = new TempFile();
            var connection = new ConnectionString { Filename = file.Filename, Password = password, CacheSize = 65536, TransactionPageLimit = 1 };
            const string predicate = "(owner.score >= 10 AND OWNER.SCORE <= 80 AND (owner.score < 20 OR OWNER.score > 70)) OR owner.score = 5";
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                rows.InsertBulk(Enumerable.Range(1, 2000).Select(i => new BsonDocument
                {
                    ["_id"] = i, ["Owner"] = new BsonDocument { ["Score"] = i % 100 }, ["Payload"] = new string('x', 800)
                }));
                rows.EnsureIndex("nested", "Owner.Score");
                db.Checkpoint();
                db.BeginTrans();
                rows.DeleteMany(predicate).Should().Be(420);
                rows.Count(predicate).Should().Be(0);
                db.Rollback();
                rows.Count(predicate).Should().Be(420);
                db.BeginTrans();
                rows.UpdateMany("{ Owner: { Score: owner.score + 61 } }", predicate).Should().Be(420);
                rows.FindAll().OrderBy(x => x["_id"]).Select(x => x["Owner"]["Score"].AsInt32).Should().Equal(
                    Enumerable.Range(1, 2000).Select(i => i % 100).Select(score => Matches(score) ? score + 61 : score));
                db.Rollback();
            }
            using (var db = new LiteDatabase(connection))
            {
                var rows = db.GetCollection("rows");
                var query = rows.Query().Where(predicate).OrderBy("owner.score", Query.Descending);
                query.GetPlan()["index"]["name"].AsString.Should().Be("nested");
                query.GetPlan().ContainsKey("filters").Should().BeFalse();
                query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
                var expected = Enumerable.Range(1, 2000).Select(i => i % 100).Where(Matches).OrderByDescending(i => i).ToArray();
                query.ToArray().Select(x => x["Owner"]["Score"].AsInt32).Should().Equal(expected);
                query.Offset(190).Limit(40).ToArray().Select(x => x["Owner"]["Score"].AsInt32).Should().Equal(expected.Skip(190).Take(40));
            }
        }

        [Fact]
        public void Member_name_identity_is_independent_of_value_collation()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
            {
                seed.GetCollection("rows").InsertBulk(new[] { "a", "A", "b", "B" }.Select(value => new BsonDocument { ["Owner"] = new BsonDocument { ["Name"] = value } }));
            }
            using var db = new LiteDatabase(file.Filename);
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("nested", "Owner.Name");
            var template = BsonExpression.Create("owner.name = @name", new BsonDocument { ["name"] = "a" });
            rows.Find(template).Select(x => x["Owner"]["Name"].AsString).Should().Equal("a");
            db.Rebuild(new RebuildOptions { Collation = new Collation("en-US/IgnoreCase") });
            rows.Query().Where(template).GetPlan()["index"]["name"].AsString.Should().Be("nested");
            rows.Find(template).Select(x => x["Owner"]["Name"].AsString).Should().BeEquivalentTo(new[] { "a", "A" });
        }

        private static bool Matches(int score) => (score >= 10 && score < 20) || (score > 70 && score <= 80) || score == 5;
    }
}
