using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1904Planning_Tests
    {
        [Fact]
        public void Included_scalar_filter_and_order_use_current_reference_values()
        {
            using var db = new LiteDatabase(":memory:");
            var targets = db.GetCollection("targets");
            targets.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["value"] = 20 },
                new BsonDocument { ["_id"] = 2, ["value"] = 10 }
            });
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["category"] = 7, ["ref"] = Reference(1) },
                new BsonDocument { ["_id"] = 2, ["category"] = 7, ["ref"] = Reference(2) }
            });
            rows.EnsureIndex("reference_value", "$.ref.value");
            rows.EnsureIndex("category");

            var filtered = rows.Include("$.ref").Query().Where("$.ref.value = 20");
            Assert.Equal("_id", filtered.GetPlan()["index"]["name"].AsString);
            Assert.Equal(1, filtered.ToDocuments().Single()["_id"].AsInt32);
            var ordered = rows.Include("$.ref").Query().OrderBy("$.ref.value");
            Assert.Equal(new[] { 2, 1 }, ordered.ToDocuments().Select(row => row["_id"].AsInt32));
            Assert.False(ordered.GetPlan()["orderBy"].IsNull);

            targets.Update(new BsonDocument { ["_id"] = 1, ["value"] = 5 });
            Assert.Empty(filtered.ToDocuments());
            Assert.Equal(new[] { 1, 2 }, ordered.ToDocuments().Select(row => row["_id"].AsInt32));

            var safe = rows.Include("$.ref").Query().Where("category = 7 AND $.ref.value = 5");
            Assert.Equal("category", safe.GetPlan()["index"]["name"].AsString);
            Assert.Equal(1, safe.ToDocuments().Single()["_id"].AsInt32);
            var stored = rows.Query().Where("$.ref.value = null");
            Assert.Equal("reference_value", stored.GetPlan()["index"]["name"].AsString);
            Assert.Equal(2, stored.Count());
        }

        [Fact]
        public void Identity_and_sibling_indexes_remain_usable_with_includes()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("targets").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 9 });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument
            {
                ["_id"] = 1, ["ref"] = Reference(1),
                ["refs"] = new BsonArray(Reference(1)),
                ["meta"] = new BsonDocument { ["owner"] = Reference(1), ["category"] = 7 }
            });
            rows.EnsureIndex("identity", "$.ref.$id");
            rows.EnsureIndex("identities", "$.refs[*].$id");
            rows.EnsureIndex("category", "$.meta.category");
            var scalar = rows.Include("$.ref").Query().Where("$.ref.$id = 1");
            Assert.Equal("identity", scalar.GetPlan()["index"]["name"].AsString);
            Assert.Equal(9, scalar.ToDocuments().Single()["ref"]["value"].AsInt32);
            var array = rows.Include("$.refs[*]").Query().Where(Query.Any().EQ("$.refs[*].$id", 1));
            Assert.Equal("identities", array.GetPlan()["index"]["name"].AsString);
            Assert.Equal(9, array.ToDocuments().Single()["refs"][0]["value"].AsInt32);
            var sibling = rows.Include("$.meta.owner").Query().Where("$.meta.category = 7");
            Assert.Equal("category", sibling.GetPlan()["index"]["name"].AsString);
            Assert.Equal(9, sibling.ToDocuments().Single()["meta"]["owner"]["value"].AsInt32);
        }

        [Theory]
        [InlineData("$id")]
        [InlineData("$ID")]
        public void Included_payload_cannot_replace_the_reference_identifier(string payloadKey)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("targets").Insert(new BsonDocument { ["_id"] = 1, [payloadKey] = 99 });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["ref"] = Reference(1), ["refs"] = new BsonArray(Reference(1)) });
            rows.EnsureIndex("identity", "$.ref.$id");
            rows.EnsureIndex("identities", "$.refs[*].$id");
            var scalar = rows.Include("$.ref").Query().Where("$.ref.$id = 1");
            Assert.Equal("identity", scalar.GetPlan()["index"]["name"].AsString);
            Assert.Equal(1, scalar.ToDocuments().Single()["ref"]["$id"].AsInt32);
            var array = rows.Include("$.refs[*]").Query().Where(Query.Any().EQ("$.refs[*].$id", 1));
            Assert.Equal("identities", array.GetPlan()["index"]["name"].AsString);
            Assert.Equal(1, array.ToDocuments().Single()["refs"][0]["$id"].AsInt32);
            Assert.Empty(rows.Include("$.ref").Query().Where("$.ref.$id = 99").ToDocuments());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Fixed_array_reference_ids_retain_their_index(int position)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("targets").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 9 });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["refs"] = new BsonArray(Reference(1)) });
            var path = "$.refs[" + position + "]";
            rows.EnsureIndex("identity", path + ".$id");
            var query = rows.Include(path).Query().Where(path + ".$id = 1");
            Assert.Equal("identity", query.GetPlan()["index"]["name"].AsString);
            Assert.Equal(9, query.ToDocuments().Single()["refs"][0]["value"].AsInt32);
        }

        private static BsonDocument Reference(int id)
        {
            return new BsonDocument { ["$id"] = id, ["$ref"] = "targets" };
        }
    }
}
