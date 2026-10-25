using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class UniqueIndexStream_Tests
    {
        [Theory]
        [InlineData("Score IN [1,1,2,2,5]")]
        [InlineData("Score = 1 OR Score = 1 OR Score = 2 OR Score = 5")]
        [InlineData("Score >= 1 AND Score <= 5 AND Score != 3 AND Score != 4")]
        public void Unique_secondary_indexes_preserve_order_counts_and_pagination(string predicate)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["Score"] = i }));
            rows.EnsureIndex("score", "Score", true);
            var query = rows.Query().Where(predicate).OrderByDescending("Score");
            query.GetPlan()["index"]["name"].AsString.Should().Be("score");
            query.ToArray().Select(x => x["Score"].AsInt32).Should().Equal(5, 2, 1);
            query.Count().Should().Be(3);
            query.Exists().Should().BeTrue();
            query.Offset(1).Limit(1).ToArray().Single()["Score"].AsInt32.Should().Be(2);
        }

        [Fact]
        public void Collated_unique_seeks_deduplicate_values_before_traversal()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/IgnoreCase") });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Code"] = "a" });
            rows.Insert(new BsonDocument { ["Code"] = "b" });
            rows.EnsureIndex("code", "Code", true);
            rows.Query().Where("Code IN ['A','a','B','b']").Count().Should().Be(2);
            rows.Query().Where("Code LIKE 'a%'").Count().Should().Be(1);
        }

        [Fact]
        public void Unique_array_values_are_single_keys_and_multikey_unique_indexes_remain_forbidden()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(1, 2) });
            rows.Insert(new BsonDocument { ["Values"] = new BsonArray(2, 3) });
            rows.EnsureIndex("values", "Values", true);
            rows.Query().Where("Values = [1,2] OR Values = [1,2] OR Values = [2,3]").Count().Should().Be(2);
            Action multikey = () => rows.EnsureIndex("multikey", "Values[*]", true);
            multikey.Should().Throw<LiteException>().WithMessage("*Multikey*");
        }

        [Fact]
        public void Persisted_unique_indexes_keep_one_result_per_document_after_updates()
        {
            var filename = Path.GetTempFileName();
            try
            {
                using (var db = new LiteDatabase(filename))
                {
                    var rows = db.GetCollection("rows");
                    rows.InsertBulk(Enumerable.Range(1, 10).Select(i => new BsonDocument { ["_id"] = i, ["Score"] = i }));
                    rows.EnsureIndex("score", "Score", true);
                    rows.Update(new BsonDocument { ["_id"] = 1, ["Score"] = 11 });
                }
                using (var db = new LiteDatabase(filename))
                {
                    var rows = db.GetCollection("rows");
                    rows.Query().Where("Score >= 2").Count().Should().Be(10);
                    rows.DeleteMany("Score IN [2,2,3,3]").Should().Be(2);
                    rows.Query().Where("Score >= 2").Count().Should().Be(8);
                }
            }
            finally { File.Delete(filename); }
        }
    }
}
