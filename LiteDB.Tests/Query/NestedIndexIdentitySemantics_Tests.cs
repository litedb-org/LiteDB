using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class NestedIndexIdentitySemantics_Tests
    {
        [Theory]
        [InlineData("Owner.Name", "OWNER.name", "Tags[*]", "TAGS[*]")]
        [InlineData("quote\"Parent", "QUOTE\"parent", "odd Field", "ODD field")]
        [InlineData("Owner", "owner", "Score.Value", "SCORE.value")]
        public void Literal_member_names_keep_dots_quotes_and_brackets(string parent, string lookupParent, string field, string lookupField)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 5).Select(i => new BsonDocument
            {
                ["_id"] = i, [parent] = new BsonDocument { [field] = i }
            }));
            rows.EnsureIndex("literal", x => x[parent][field]);
            var query = rows.Query().Where(x => x[lookupParent][lookupField] >= 3);
            query.GetPlan()["index"]["name"].AsString.Should().Be("literal");
            query.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(3, 4, 5);
        }

        [Theory]
        [InlineData("IIF(Owner.Kind = 'A', Owner.Left.Score, Owner.Right.Score)", "IIF(Owner.Kind = 'a', Owner.Left.Score, Owner.Right.Score) = 3")]
        [InlineData("Owner.Items[Kind = 'A'].Score", "Owner.Items[Kind = 'a'].Score ANY = 3")]
        [InlineData("Owner.Items[0].Score", "owner.items[0].score = 3")]
        [InlineData("Owner.Items[*].Score", "owner.items[*].score ANY = 3")]
        public void Computed_literals_and_array_selectors_keep_distinct_index_identities(string index, string predicate)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument
            {
                ["_id"] = 1, ["Owner"] = new BsonDocument
                {
                    ["Kind"] = "a", ["Left"] = new BsonDocument { ["Score"] = 3 }, ["Right"] = new BsonDocument { ["Score"] = 99 },
                    ["Items"] = new BsonArray(new BsonDocument { ["Kind"] = "a", ["Score"] = 3 })
                }
            });
            var expected = rows.Find(predicate).Select(x => x["_id"]).ToArray();
            expected.Should().HaveCount(1);
            rows.EnsureIndex("other", index);
            var query = rows.Query().Where(predicate);
            query.GetPlan()["index"]["name"].AsString.Should().Be("_id");
            query.ToArray().Select(x => x["_id"]).Should().Equal(expected);
        }

        [Theory]
        [InlineData(32, "nested")]
        [InlineData(65, "_id")]
        public void Member_path_proof_has_a_bounded_depth(int depth, string expectedIndex)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1 });
            var path = string.Join(".", Enumerable.Repeat("Child", depth));
            rows.EnsureIndex("nested", path);
            var query = rows.Query().Where(path.ToLowerInvariant() + " = null");
            query.GetPlan()["index"]["name"].AsString.Should().Be(expectedIndex);
            query.ToArray().Single()["_id"].AsInt32.Should().Be(1);
        }

        [Fact]
        public void Matching_parenthesized_member_paths_reuse_the_index()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Owner"] = new BsonDocument { ["Score"] = 3 } });
            rows.EnsureIndex("nested", "(Owner.Score)");
            var query = rows.Query().Where("(owner.score) = 3");
            query.GetPlan()["index"]["name"].AsString.Should().Be("nested");
            query.Count().Should().Be(1);
        }
    }
}
