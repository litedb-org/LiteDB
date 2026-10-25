using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2583OrderAlias_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Aliases_use_computed_values_with_parameters_and_multiple_sort_columns(bool indexed)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["amount"] = 3, ["rank"] = 10, ["tie"] = 2 },
                new BsonDocument { ["_id"] = 2, ["amount"] = 8, ["rank"] = 30, ["tie"] = 1 },
                new BsonDocument { ["_id"] = 3, ["amount"] = 3, ["rank"] = 20, ["tie"] = 1 }
            });
            if (indexed) rows.EnsureIndex("rank");
            using var result = db.Execute("SELECT _id, @0 - amount AS rank FROM rows ORDER BY RANK, tie LIMIT 2 OFFSET 1", 100);
            var values = result.ToArray();
            values.Select(x => x["_id"].AsInt32).Should().Equal(3, 1);
            values.Select(x => x["rank"].AsInt32).Should().Equal(97, 97);
        }

        [Theory]
        [InlineData("a-b")]
        [InlineData("Last Job This Year")]
        [InlineData("123")]
        public void Unusual_default_projection_names_do_not_break_unrelated_ordering(string field)
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, [field] = "first" },
                new BsonDocument { ["_id"] = 2, [field] = "second" }
            });
            using var result = db.Execute("SELECT $.[" + JsonSerializer.Serialize(field) + "] FROM rows ORDER BY _id DESC");
            result.ToArray().Select(x => x[field].AsString).Should().Equal("second", "first");
        }

        [Fact]
        public void Explicit_root_path_orders_source_field_when_alias_collides()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["amount"] = 3, ["rank"] = 1 },
                new BsonDocument { ["_id"] = 2, ["amount"] = 8, ["rank"] = 2 }
            });
            using var result = db.Execute("SELECT _id, 100 - amount AS rank FROM rows ORDER BY $.rank");
            result.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(1, 2);
        }

        [Fact]
        public void Group_alias_ordering_and_unselected_source_fields_remain_valid()
        {
            using var db = new LiteDatabase(":memory:");
            db.GetCollection("rows").Insert(new[]
            {
                new BsonDocument { ["_id"] = 1, ["amount"] = 3 },
                new BsonDocument { ["_id"] = 2, ["amount"] = 8 }
            });
            using var source = db.Execute("SELECT _id FROM rows ORDER BY amount DESC");
            source.ToArray().Select(x => x["_id"].AsInt32).Should().Equal(2, 1);
            using var grouped = db.Execute("SELECT @key AS key, COUNT(*) AS count FROM rows GROUP BY amount ORDER BY key DESC");
            grouped.ToArray().Select(x => x["key"].AsInt32).Should().Equal(8, 3);
        }
    }
}
