using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    /// <summary>
    /// The same predicate over the same logical documents must select the same
    /// documents whether they are stored as BSON (Legacy) or compact (Auto), and
    /// whether or not the engine evaluates it over borrowed BSON.
    /// </summary>
    public class CompactStorageQueryParity_Tests
    {
        private readonly ITestOutputHelper _output;

        public CompactStorageQueryParity_Tests(ITestOutputHelper output) => _output = output;

        private static readonly BsonValue[] Values = new BsonValue[]
        {
            BsonValue.Null, BsonValue.MinValue, BsonValue.MaxValue, 0, 1, -1, 5, int.MaxValue, long.MinValue,
            double.NaN, double.PositiveInfinity, -0.0, 0.5, 1.00m, -0.000m, decimal.MinValue,
            // Doubles whose decimal conversion rounds (15 significant digits or underflow).
            double.Epsilon, 1e-30, 5.000000000000001, 4.999999999999999, 0.30000000000000004,
            "", "abc", new byte[0], new byte[] { 1 }, Guid.Empty, ObjectId.Empty, true, false,
            DateTime.MinValue, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new BsonVector(new[] { 1f, 2f }), new BsonDocument { ["x"] = 1 }, new BsonArray { 1, 2 }
        }.Concat(Enumerable.Range(0, CompactStorageDifferential_Tests.EdgeScalarCases)
            .Select(kind => CompactStorageDifferential_Tests.EdgeScalar(kind, new Random(kind)))).ToArray();

        private static BsonDocument Doc(int id, BsonValue value) => new BsonDocument
        {
            ["_id"] = id,
            ["MeasurementValue"] = value,
            ["PaddingPropertyNumberOne"] = id,
            ["PaddingPropertyNumberTwo"] = "padding",
            ["PaddingPropertyNumberThree"] = id * 2
        };

        private static int[] Run(CompactStorageMode mode, IEnumerable<BsonDocument> docs, string predicate, bool index, out int schemaPages)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", CompactStorage = mode });
            var col = db.GetCollection("docs");
            col.InsertBulk(docs.Select(d => new BsonDocument(d.ToDictionary(x => x.Key, x => x.Value))));
            if (index) col.EnsureIndex("measurement", "$.MeasurementValue");
            schemaPages = db.Execute("SELECT COUNT(*) FROM $dump WHERE $.pageType = 'Schema'").ToEnumerable().Single().AsDocument.Values.Single().AsInt32;
            return col.Find(predicate).Select(d => d["_id"].AsInt32).OrderBy(x => x).ToArray();
        }

        private string Show(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => $"{id}:{Values[id - 1].Type}:{Values[id - 1]}"));

        [Theory]
        [InlineData("$.MeasurementValue > 5")]
        [InlineData("$.MeasurementValue <= 5")]
        [InlineData("$.MeasurementValue = 0")]
        [InlineData("$.MeasurementValue > 0")]
        [InlineData("$.MeasurementValue != 0")]
        [InlineData("$.MeasurementValue < 1")]
        [InlineData("$.MeasurementValue > 'a'")]
        [InlineData("$.MeasurementValue = null")]
        public void Predicate_selects_the_same_documents_for_legacy_and_compact_storage(string predicate)
        {
            var docs = Values.Select((v, i) => Doc(i + 1, v)).ToList();
            var expression = BsonExpression.Create(predicate);
            var reference = docs.Where(d => expression.ExecuteScalar(d) is var r && r.IsBoolean && r.AsBoolean)
                .Select(d => d["_id"].AsInt32).ToArray();

            var legacy = Run(CompactStorageMode.Legacy, docs, predicate, index: false, out var legacySchemas);
            var auto = Run(CompactStorageMode.Auto, docs, predicate, index: false, out var autoSchemas);
            legacySchemas.Should().Be(0);
            autoSchemas.Should().BeGreaterThan(0, "the Auto documents must actually be stored compact");

            _output.WriteLine("in-memory expression only: " + Show(reference.Except(legacy).Union(reference.Except(auto))));
            _output.WriteLine("Legacy only: " + Show(legacy.Except(auto)));
            _output.WriteLine("Auto only:   " + Show(auto.Except(legacy)));

            auto.Should().Equal(legacy, "the storage representation must not change which documents a predicate selects");
        }

        [Theory]
        [InlineData("$.MeasurementValue > 5")]
        [InlineData("$.MeasurementValue = 0")]
        public void Legacy_scan_index_and_expression_agree_on_mixed_number_predicates(string predicate)
        {
            var docs = Values.Select((v, i) => Doc(i + 1, v)).Where(d => d["MeasurementValue"].IsNumber).ToList();
            var expression = BsonExpression.Create(predicate);
            var reference = docs.Where(d => expression.ExecuteScalar(d) is var r && r.IsBoolean && r.AsBoolean)
                .Select(d => d["_id"].AsInt32).ToArray();

            var scan = Run(CompactStorageMode.Legacy, docs, predicate, index: false, out _);
            var seek = Run(CompactStorageMode.Legacy, docs, predicate, index: true, out _);

            _output.WriteLine("expression only: " + Show(reference.Except(scan)));
            _output.WriteLine("scan only:       " + Show(scan.Except(reference)));
            _output.WriteLine("index != expression: " + Show(seek.Except(reference).Union(reference.Except(seek))));

            seek.Should().Equal(reference, "an index seek must use the same number semantics as the expression engine");
            scan.Should().Equal(reference, "a full scan over BSON must use the same number semantics as the expression engine");
        }
    }
}
