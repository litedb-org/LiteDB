using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2892_Tests
    {
        [Fact]
        public void Documents_with_different_keys_have_antisymmetric_ordering()
        {
            var left = new BsonDocument { ["x"] = 1 };
            var right = new BsonDocument { ["y"] = 1 };

            var forward = left.CompareTo(right);
            var reverse = right.CompareTo(left);

            forward.Should().Be(-reverse);
            forward.Should().NotBe(0);
        }

        [Fact]
        public void Null_values_do_not_make_different_document_keys_equal()
        {
            var left = new BsonDocument { ["left"] = BsonValue.Null };
            var right = new BsonDocument { ["right"] = BsonValue.Null };

            left.Equals(right).Should().BeFalse();
            left.CompareTo(right).Should().NotBe(0);
        }

        [Fact]
        public void Document_order_ignores_insertion_order_and_key_case_but_includes_names()
        {
            var first = new BsonDocument { ["x"] = 1, ["y"] = 0 };
            var second = new BsonDocument { ["y"] = 1, ["x"] = 0 };
            first.CompareTo(second).Should().Be(1);
            second.CompareTo(first).Should().Be(-1);
            var equivalent = new BsonDocument { ["Y"] = 0L, ["X"] = 1L };
            first.Equals(equivalent).Should().BeTrue();
            first.GetHashCode().Should().Be(equivalent.GetHashCode());
            var left = new BsonDocument { ["x"] = new BsonArray { "A" } };
            var right = new BsonDocument { ["X"] = new BsonArray { "a" } };
            left.CompareTo(right, new Collation("en-US/IgnoreCase")).Should().Be(0);
            left.CompareTo(right, Collation.Binary).Should().NotBe(0);
        }

        [Fact]
        public void Canonical_document_order_is_transitive_across_different_key_sets()
        {
            var documents = new[]
            {
                new BsonDocument(), new BsonDocument { ["x"] = BsonValue.Null },
                new BsonDocument { ["y"] = BsonValue.Null }, new BsonDocument { ["x"] = 1 },
                new BsonDocument { ["y"] = 1 }, new BsonDocument { ["y"] = 0, ["x"] = 1 },
                new BsonDocument { ["x"] = 0, ["y"] = 1 }
            };
            foreach (var a in documents)
            foreach (var b in documents)
            foreach (var c in documents)
            {
                Math.Sign(a.CompareTo(b)).Should().Be(-Math.Sign(b.CompareTo(a)));
                if (a.CompareTo(b) <= 0 && b.CompareTo(c) <= 0) a.CompareTo(c).Should().BeLessOrEqualTo(0);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Document_index_keys_remain_searchable_after_reopen_in_either_insert_order(bool reverse)
        {
            using var file = new TempFile();
            var keys = new[]
            {
                new BsonDocument { ["a"] = BsonValue.Null }, new BsonDocument { ["b"] = BsonValue.Null },
                new BsonDocument { ["x"] = 1, ["y"] = 0 }, new BsonDocument { ["y"] = 1, ["x"] = 0 }
            };
            using (var db = new LiteDatabase(file.Filename))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("key", true);
                foreach (var i in reverse ? Enumerable.Range(0, keys.Length).Reverse() : Enumerable.Range(0, keys.Length))
                    rows.Insert(new BsonDocument { ["_id"] = i + 1, ["key"] = keys[i] });
            }
            using var reopened = new LiteDatabase(file.Filename);
            for (var i = 0; i < keys.Length; i++)
                reopened.GetCollection("rows").Find(Query.EQ("key", keys[i])).Single()["_id"].AsInt32.Should().Be(i + 1);
            reopened.GetCollection("rows").Find(Query.All("key")).Select(row => row["_id"].AsInt32)
                .Should().Equal(1, 2, 4, 3);
        }
    }
}
