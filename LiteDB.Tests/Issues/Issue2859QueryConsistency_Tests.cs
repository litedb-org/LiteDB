using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2859QueryConsistency_Tests
    {
        private static readonly Collation IgnoreCase = new Collation("en-US/IgnoreCase");
        private static BsonValue Key(string value, bool array) => array ? (BsonValue)new BsonArray { 1, value } : new BsonDocument { ["leaf"] = value };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Scalar_ranges_match_leaf_oracle_before_and_after_indexing(bool array)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = IgnoreCase });
            var rows = db.GetCollection("rows");
            var values = new[] { "A", "a", "Zebra", "apple" };
            for (var id = 0; id < values.Length; id++) rows.Insert(new BsonDocument { ["_id"] = id + 1, ["value"] = Key(values[id], array) });
            for (var indexed = 0; indexed < 2; indexed++)
            {
                foreach (var cutoff in new[] { "a", "apple", "zebra" })
                foreach (var op in new[] { ">", ">=", "<", "<=" })
                {
                    Func<int, bool> match = comparison => op == ">" ? comparison > 0 : op == ">=" ? comparison >= 0 : op == "<" ? comparison < 0 : comparison <= 0;
                    var expected = values.Select((value, id) => new { value, id }).Where(item => match(IgnoreCase.Compare(item.value, cutoff))).Select(item => item.id + 1);
                    rows.Find(BsonExpression.Create("value " + op + " @0", Key(cutoff, array))).Select(row => row["_id"].AsInt32).OrderBy(id => id).Should().Equal(expected);
                }
                rows.EnsureIndex("value");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Indexed_IN_deduplicates_collation_equivalent_operands(bool array)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = IgnoreCase });
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = Key("a", array) });
            rows.EnsureIndex("value");
            rows.Find(Query.In("value", Key("a", array), Key("A", array))).Select(row => row["_id"].AsInt32).Should().Equal(1);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Multikey_queries_deduplicate_documents_and_unique_multikey_remains_unsupported(bool array, bool unique)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = IgnoreCase });
            var rows = db.GetCollection("rows");
            if (unique)
            {
                Action create = () => rows.EnsureIndex("keys", "values[*]", true);
                create.Should().Throw<LiteException>().WithMessage("*Multikey*unique*");
                return;
            }
            rows.EnsureIndex("keys", "values[*]", false);
            rows.Insert(new BsonDocument { ["_id"] = 1, ["values"] = new BsonArray { Key("a", array), Key("A", array) } });
            rows.Find(BsonExpression.Create("values[*] ANY = @0", Key("A", array))).Select(row => row["_id"].AsInt32).Should().Equal(1);
        }

        [Fact]
        public void Mixed_type_index_keys_keep_normalized_type_order()
        {
            var values = new BsonValue[] { new BsonArray { "a" }, "z", new BsonDocument { ["leaf"] = "a" }, true, 42 };
            foreach (var left in values)
            foreach (var right in values)
                IgnoreCase.Compare(left, right).Should().Be(Math.Sign((int)left.Type - (int)right.Type));
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = IgnoreCase });
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value");
            for (var id = 0; id < values.Length; id++) rows.Insert(new BsonDocument { ["_id"] = id + 1, ["value"] = values[id] });
            for (var id = 0; id < values.Length; id++) rows.Find(Query.EQ("value", values[id])).Select(row => row["_id"].AsInt32).Should().Equal(id + 1);
        }
    }
}
