using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CompactWritePreparation_Tests
    {
        [Fact]
        public void Fixed_corpus_keeps_pre_optimization_payload_and_schema_bytes()
        {
            // Captured from a2664ea41, before changing the compact writer.
            using var output = new MemoryStream();
            using var binary = new BinaryWriter(output);
            var catalog = new SchemaCatalog();
            foreach (var document in Corpus())
            {
                using var encoder = new CompactDocumentWriter(catalog);
                var payload = encoder.Encode(document);
                binary.Write(payload.Length);
                binary.Write(payload);
                binary.Write(encoder.Pending.Count);
                foreach (var schema in encoder.Pending)
                {
                    binary.Write(schema.Id);
                    binary.Write(schema.Fingerprint);
                    binary.Write(schema.Definition.Length);
                    binary.Write(schema.Definition);
                    catalog.Add(schema);
                }
            }
            using var expected = typeof(CompactWritePreparation_Tests).Assembly.GetManifestResourceStream(
                "LiteDB.Tests.Resources.CompactWrite_a2664ea41.bin");
            using var before = new MemoryStream();
            expected.CopyTo(before);
            output.ToArray().Should().Equal(before.ToArray());
        }

        [Fact]
        public void Reused_encoder_preserves_payloads_and_schema_selection_across_writes()
        {
            var catalog = new SchemaCatalog();
            var freshCatalog = new SchemaCatalog();
            using var reused = new CompactDocumentWriter(catalog);
            var retained = new List<(byte[] Payload, byte[] Copy)>();
            foreach (var document in Corpus())
            {
                using var fresh = new CompactDocumentWriter(freshCatalog);
                var expected = fresh.Encode(document);
                var actual = reused.Encode(document);
                actual.Should().Equal(expected);
                retained.Add((actual, (byte[])actual.Clone()));
                reused.Pending.Select(s => s.Fingerprint).Should().Equal(fresh.Pending.Select(s => s.Fingerprint));
                foreach (var schema in fresh.Pending) freshCatalog.Add(schema);
                foreach (var schema in reused.Pending) catalog.Add(schema);
                reused.Reset();
                reused.Pending.Should().BeEmpty();
            }
            foreach (var item in retained) item.Payload.Should().Equal(item.Copy);
        }

        [Fact]
        public void Rejection_and_encoding_failure_cannot_reuse_unpublished_pending_schemas()
        {
            var catalog = new SchemaCatalog();
            using var encoder = new CompactDocumentWriter(catalog, _ => true);
            var document = CompactStorage_Tests.Document(1);
            encoder.Encode(document, 1).Should().BeNull();
            encoder.Pending.Should().ContainSingle();
            encoder.Reset();
            encoder.Pending.Should().BeEmpty();
            var malformed = CompactStorage_Tests.Document(2);
            malformed["InvalidString"] = "unpaired\ud800";
            Action encode = () => encoder.Encode(malformed);
            encode.Should().Throw<System.Text.EncoderFallbackException>();
            encoder.Pending.Should().ContainSingle();
            encoder.Reset();
            catalog.Count.Should().Be(0);
            var replacement = new BsonDocument { ["DifferentLongPropertyName"] = 42 };
            var bytes = encoder.Encode(replacement);
            encoder.Pending.Should().ContainSingle().Which.Id.Should().Be(1);
            using var fresh = new CompactDocumentWriter(new SchemaCatalog(), _ => true);
            bytes.Should().Equal(fresh.Encode(replacement));
        }

        [Fact]
        public void Encoding_respects_exact_size_cutoff_and_reset_bounds_retained_buffers()
        {
            using var encoder = new CompactDocumentWriter(new SchemaCatalog(), _ => true);
            var document = CompactStorage_Tests.Document(1);
            var expected = encoder.Encode(document);
            encoder.Encode(document, expected.Length).Should().Equal(expected);
            encoder.Encode(document, expected.Length - 1).Should().BeNull();
            var large = new BsonDocument { ["LargeText"] = new string('世', 100000) };
            var largeBytes = encoder.Encode(large);
            using (var reader = new BufferReader(largeBytes))
                DocumentStorageCodec.Read(reader, catalog: () =>
                {
                    var catalog = new SchemaCatalog();
                    foreach (var schema in encoder.Pending) catalog.Add(schema);
                    return catalog;
                }).GetValue()["LargeText"].AsString.Should().Be(large["LargeText"].AsString);
            encoder.Reset();
            var stream = (MemoryStream)typeof(CompactDocumentWriter).GetField("_stream",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(encoder);
            stream.Capacity.Should().BeLessThanOrEqualTo(64 * 1024);
            stream.Length.Should().Be(0);
            encoder.Encode(document).Should().Equal(expected);
        }

        [Fact]
        public void One_off_scalar_shape_is_rejected_before_allocating_an_output_buffer()
        {
            using var encoder = new CompactDocumentWriter(new SchemaCatalog(), _ => false);
            encoder.Encode(CompactStorage_Tests.Document(1)).Should().BeNull();
            encoder.Pending.Should().BeEmpty();
            var stream = (MemoryStream)typeof(CompactDocumentWriter).GetField("_stream",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(encoder);
            stream.Capacity.Should().Be(0);
            stream.Length.Should().Be(0);
        }

        [Fact]
        public void Prepared_schema_reuse_requires_exact_shape_and_current_catalog_generation()
        {
            var catalog = new SchemaCatalog();
            var admissions = 0;
            using var encoder = new CompactDocumentWriter(catalog, _ => { admissions++; return true; });
            var document = CompactStorage_Tests.Document(1);
            encoder.Encode(document, 1).Should().BeNull();
            var first = encoder.Pending.Single();
            encoder.Encode(document, 1).Should().BeNull();
            encoder.Pending.Single().Should().BeSameAs(first);
            admissions.Should().Be(2, "cached preparation must still observe each shape admission");

            var partial = new BsonDocument { ["_id"] = 2, ["RepeatedPropertyName1"] = 1 };
            var partialBytes = encoder.Encode(partial);
            var partialSchema = encoder.Pending.Single();
            partialSchema.Should().NotBeSameAs(first, "an unpublished superset must not change the selected definition");
            partialSchema.Fields.Should().HaveCount(2);
            using (var fresh = new CompactDocumentWriter(new SchemaCatalog(), _ => true))
                partialBytes.Should().Equal(fresh.Encode(partial));
            catalog.Add(partialSchema);
            encoder.Reset();
            encoder.Encode(document);
            var next = encoder.Pending.Single();
            next.Id.Should().Be(2, "publishing a schema advances the candidate generation");
            next.Should().NotBeSameAs(first);
            catalog.Add(next);
            encoder.Reset();
            encoder.Encode(document);
            encoder.Pending.Should().BeEmpty("the published catalog now owns the complete shape");
            catalog.Get(1, partialSchema.Fingerprint).Should().BeSameAs(partialSchema);
            catalog.Get(2, next.Fingerprint).Should().BeSameAs(next);
        }

        private static IEnumerable<BsonDocument> Corpus()
        {
            var full = new BsonDocument { ["last"] = 1, ["_ID"] = 17 };
            var values = new BsonValue[]
            {
                BsonValue.Null, BsonValue.MinValue, BsonValue.MaxValue, int.MinValue, long.MaxValue,
                double.NaN, double.PositiveInfinity, -0.0, 1.234567890123456789m, "héllo 世界\0",
                new byte[] { 0, 1, 255 }, new Guid("01234567-89ab-cdef-0123-456789abcdef"),
                new ObjectId("0123456789abcdef01234567"), true, false, DateTime.MinValue, DateTime.MaxValue,
                new DateTime(2026, 9, 1, 2, 3, 4, DateTimeKind.Utc), new BsonVector(new[] { 1f, -2f, 0f }),
                new BsonArray { 1, "two", BsonValue.Null, new BsonDocument { ["Nested"] = 1 } }
            };
            for (var i = 0; i < values.Length; i++) full["LongProperty" + i] = values[i];
            yield return full;
            yield return full;
            yield return new BsonDocument { ["LongProperty9"] = "世界", ["_id"] = 2 };
            yield return new BsonDocument { ["_id"] = 3, ["LongProperty1"] = BsonValue.Null, ["LongProperty19"] = values[19] };
            yield return new BsonDocument { ["longproperty9"] = "case-sensitive schema" };
            yield return new BsonDocument { ["LongProperty9"] = "reordered", ["last"] = 4 };
            yield return new BsonDocument();
            yield return new BsonDocument { ["nested"] = full, ["array"] = new BsonArray { full, new BsonDocument() } };
            yield return new BsonDocument { ["nested"] = full, ["array"] = new BsonArray { full, new BsonDocument() } };
            yield return new BsonDocument { [new string('n', 513)] = 1 };
            yield return new BsonDocument { [new string('n', 513)] = 2 };
        }
    }
}
