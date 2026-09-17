using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class ExtendedSortKey_Tests
    {
        [Theory]
        [InlineData(false, 255)]
        [InlineData(false, 256)]
        [InlineData(false, 511)]
        [InlineData(false, 512)]
        [InlineData(false, 768)]
        [InlineData(false, 1021)]
        [InlineData(true, 255)]
        [InlineData(true, 256)]
        [InlineData(true, 512)]
        [InlineData(true, 1021)]
        public void Full_sorts_decode_extended_string_and_binary_key_lengths(bool binary, int length)
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var documents = Enumerable.Range(1, 2000).Select(i =>
            {
                var value = i * 37 % 2000;
                BsonValue key;
                if (binary)
                {
                    var bytes = new byte[length];
                    bytes[length - 2] = (byte)(value >> 8);
                    bytes[length - 1] = (byte)value;
                    key = bytes;
                }
                else
                {
                    key = new string('x', length - 5) + value.ToString("D5");
                }
                return new BsonDocument { ["_id"] = i, ["Key"] = key };
            }).ToArray();
            rows.InsertBulk(documents);
            var expected = documents.OrderBy(x => x["Key"], Collation.Binary).Select(x => x["_id"].AsInt32).ToArray();
            rows.Query().OrderBy("Key").ToArray().Select(x => x["_id"].AsInt32).Should().Equal(expected);
            rows.Query().OrderBy("Key", Query.Descending).Offset(200).Limit(1200).ToArray()
                .Select(x => x["_id"].AsInt32).Should().Equal(expected.AsEnumerable().Reverse().Skip(200).Take(1200));
        }

        [Fact]
        public void Extended_length_counts_utf8_bytes_and_preserves_the_following_sort_address()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection("rows");
            var prefix = new string('é', 200);
            rows.InsertBulk(Enumerable.Range(1, 3000).Select(i => new BsonDocument
            {
                ["_id"] = i, ["Key"] = prefix + (3001 - i).ToString("D4")
            }));
            rows.Query().OrderBy("Key").ToArray().Select(x => x["_id"].AsInt32)
                .Should().Equal(Enumerable.Range(1, 3000).Reverse());
        }
    }
}
