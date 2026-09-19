using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class NestedIndexInclude_Tests
    {
        [Theory]
        [InlineData("owner.score = 10")]
        [InlineData("owner.score >= 8 AND OWNER.SCORE <= 12")]
        [InlineData("owner.score = 10 OR OWNER.SCORE = 99")]
        [InlineData("Owner.Score = 10")]
        [InlineData("Owner.Score >= 8 AND Owner.Score <= 12")]
        [InlineData("Owner.Score = 10 OR Owner.Score = 99")]
        public void Resolved_members_are_filtered_using_included_values(string predicate)
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            var expected = rows.Query().Include("Owner").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            expected.Should().Equal(new BsonValue[] { 1 });
            rows.EnsureIndex("nested", "Owner.Score");
            var query = rows.Query().Include("Owner").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(x => x["_id"]).Should().Equal(expected);
        }

        [Fact]
        public void Resolved_members_use_their_actual_values_for_ordering()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("nested", "Owner.Score");
            var query = rows.Query().Include("Owner").OrderBy("owner.score");
            query.GetPlan().ContainsKey("orderBy").Should().BeTrue();
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(2, 1, 3);
        }

        [Fact]
        public void Sibling_paths_still_seek_and_order_using_their_stored_index()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Owner: { Score: _id, Manager: { $id: _id, $ref: 'owners' } } }", "true");
            rows.EnsureIndex("nested", "Owner.Score");
            var query = rows.Query().Include("OWNER.manager").Where("owner.score >= 2 AND OWNER.SCORE <= 3").OrderBy("owner.score", Query.Descending);
            query.GetPlan()["index"]["name"].AsString.Should().Be("nested");
            query.GetPlan().ContainsKey("filters").Should().BeFalse();
            query.GetPlan().ContainsKey("orderBy").Should().BeFalse();
            var result = query.ToArray();
            result.Select(x => x["_id"].AsInt32).Should().Equal(3, 2);
            result.Select(x => x["Owner"]["Manager"]["Score"].AsInt32).Should().Equal(20, 5);
            rows.Query().Include("Owner.Manager").OrderBy("owner.score").GetPlan()["index"]["name"].AsString.Should().Be("nested");
        }

        [Fact]
        public void Unrelated_indexes_remain_usable_but_included_sort_values_need_sorting()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Label: 'keep' }", "true");
            rows.EnsureIndex("label", "Label");
            rows.EnsureIndex("nested", "Owner.Score");
            var query = rows.Query().Include("Owner").Where("label = 'keep'").OrderBy("owner.score");
            query.GetPlan()["index"]["name"].AsString.Should().Be("label");
            query.GetPlan().ContainsKey("orderBy").Should().BeTrue();
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(2, 1, 3);
        }

        [Theory]
        [InlineData("Owner.Score + 1", "Owner.Score + 1 = 11", "_id")]
        [InlineData("Owner.$id", "Owner.$id = 1", "nested")]
        public void Computed_keys_and_reference_members_cannot_replace_included_values(string index, string predicate, string expectedIndex)
        {
            using var db = CreateDatabase();
            db.GetCollection("owners").UpdateMany("{ $id: _id * 10 }", "true");
            var rows = db.GetCollection("rows");
            var expected = rows.Query().Include("Owner").Where(predicate).ToArray().Select(x => x["_id"]).ToArray();
            expected.Should().Equal(new BsonValue[] { 1 });
            rows.EnsureIndex("nested", index);
            var query = rows.Query().Include("Owner").Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be(expectedIndex);
            query.ToArray().Select(x => x["_id"]).Should().Equal(expected);
        }

        [Fact]
        public void Array_includes_keep_residual_filters_for_overlapping_keys()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Owners: [Owner] }", "true");
            rows.EnsureIndex("nested", "Owners[0].Score");
            var query = rows.Query().Include("Owners[*]").Where("Owners[0].Score = 10");
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1);
        }

        [Fact]
        public void Parent_projections_keep_the_included_descendant()
        {
            using var db = CreateDatabase();
            var rows = db.GetCollection("rows");
            rows.UpdateMany("{ Owner: { Manager: Owner } }", "true");
            rows.EnsureIndex("parent", "Owner");
            var query = rows.Query().Include("Owner.Manager").Select("Owner");
            query.GetPlan()["lookup"]["loader"].AsString.Should().Be("document");
            query.ToArray().Select(x => x["Manager"]["Score"].AsInt32).Should().Equal(10, 5, 20);
        }

        private static LiteDatabase CreateDatabase()
        {
            var db = new LiteDatabase(":memory:");
            var scores = new[] { 10, 5, 20 };
            db.GetCollection("owners").InsertBulk(Enumerable.Range(1, 3).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = scores[i - 1] }));
            db.GetCollection("rows").InsertBulk(Enumerable.Range(1, 3).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Owner"] = new BsonDocument { ["$id"] = i, ["$ref"] = "owners", ["Score"] = i }
            }));
            return db;
        }
    }
}
