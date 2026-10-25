using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactCodec_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Random_shapes_order_casing_optional_fields_and_all_values_match_public_bson(bool utcDate)
        {
            var catalog = new SchemaCatalog();
            var random = new Random(2920);
            var values = new BsonValue[]
            {
                BsonValue.Null, BsonValue.MinValue, BsonValue.MaxValue, int.MinValue, long.MaxValue,
                double.NaN, double.PositiveInfinity, -0.0, 1.234567890123456789m, "héllo 世界\0",
                new byte[] { 0, 1, 255 }, Guid.NewGuid(), ObjectId.NewObjectId(), true, false,
                DateTime.MinValue, DateTime.MaxValue, new DateTime(2026, 9, 1, 2, 3, 4, DateTimeKind.Utc),
                new BsonVector(new[] { 1f, -2f, 0f }),
                new BsonDocument { ["NestedLongField"] = "value" },
                new BsonArray { 1, "two", BsonValue.Null, new BsonDocument { ["Nested"] = 1 } }
            };
            for (var trial = 0; trial < 500; trial++)
            {
                var doc = new BsonDocument { ["_id"] = trial };
                foreach (var i in Enumerable.Range(0, 20).OrderBy(_ => trial % 3 == 0 ? random.Next() : 0))
                {
                    if (trial % 5 != 0 && random.Next(5) < 2) continue;
                    doc[(trial % 7 == 0 ? "FIELD" : "Field") + i] = values[random.Next(values.Length)];
                }
                byte[] payload;
                using (var encoder = new CompactDocumentWriter(catalog))
                {
                    payload = encoder.Encode(doc);
                    foreach (var schema in encoder.Pending) catalog.Add(schema);
                }
                using var reader = new BufferReader(payload, utcDate);
                var actual = DocumentStorageCodec.Read(reader, null, () => catalog, utcDate).GetValue();
                var expected = BsonSerializer.Deserialize(BsonSerializer.Serialize(doc), utcDate);
                BsonSerializer.Serialize(actual).Should().Equal(BsonSerializer.Serialize(expected));
                actual.Keys.Should().Equal(expected.Keys);
            }
        }

        [Fact]
        public void Optional_fields_reuse_ordered_superset_and_null_remains_present()
        {
            var catalog = new SchemaCatalog();
            var full = new BsonDocument { ["_id"] = 1, ["LongFieldAlpha"] = 1, ["LongFieldBeta"] = 2 };
            Encode(full, catalog);
            Encode(full, catalog);
            catalog.Count.Should().Be(1);
            var partial = new BsonDocument { ["_id"] = 2, ["LongFieldBeta"] = BsonValue.Null };
            var payload = Encode(partial, catalog);
            catalog.Count.Should().Be(1);
            using var reader = new BufferReader(payload);
            var actual = DocumentStorageCodec.Read(reader, null, () => catalog).GetValue();
            actual.ContainsKey("LongFieldAlpha").Should().BeFalse();
            actual.ContainsKey("LongFieldBeta").Should().BeTrue();
            actual["LongFieldBeta"].IsNull.Should().BeTrue();
        }

        [Fact]
        public void Corrupt_schema_references_flags_counts_and_truncation_fail_with_context()
        {
            var catalog = new SchemaCatalog();
            var document = CompactStorage_Tests.Document(1);
            Encode(document, catalog);
            var payload = Encode(document, catalog);
            foreach (var offset in new[] { 0, 4, 5, 6, 10, 14 })
            {
                var damaged = (byte[])payload.Clone();
                damaged[offset] ^= 0x40;
                Action read = () =>
                {
                    using var reader = new BufferReader(damaged);
                    DocumentStorageCodec.Read(reader, null, () => catalog, false, "docs", new PageAddress(42, 1)).GetValue();
                };
                read.Should().Throw<LiteException>().WithMessage("*collection 'docs'*schema*42*");
            }
            for (var length = 10; length < payload.Length; length++)
            {
                var damaged = payload.Take(length).ToArray();
                Action read = () =>
                {
                    using var reader = new BufferReader(damaged);
                    DocumentStorageCodec.Read(reader, null, () => catalog, false, "docs").GetValue();
                };
                read.Should().Throw<LiteException>();
            }
        }

        internal static byte[] Encode(BsonDocument document, SchemaCatalog catalog)
        {
            using var encoder = new CompactDocumentWriter(catalog);
            var bytes = encoder.Encode(document);
            foreach (var schema in encoder.Pending) catalog.Add(schema);
            return bytes;
        }
    }
}
