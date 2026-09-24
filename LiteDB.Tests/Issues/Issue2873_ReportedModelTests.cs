#if NETCOREAPP
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2873_ReportedModelTests
    {
        [Fact]
        public void Reported_Enum_model_survives_custom_construction_and_independent_reopen()
        {
            using var file = new TempFile();
            var receipts = new[]
            {
                new QuantityRange<Mass>(100, 500, Mass.Units.Pound),
                new QuantityRange<Mass>(-12.5, 81.75, Mass.Units.Kilogram)
            };
            var keys = new List<BsonValue>();
            using (var writer = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var rows = writer.GetCollection<QuantityRange<Mass>>("ranges");
                foreach (var receipt in receipts)
                {
                    keys.Add(rows.Insert(receipt));
                }
            }
            keys.Distinct().Count().Should().Be(2);

            var constructed = new List<QuantityRange<Mass>>();
            Func<BsonDocument, QuantityRange<Mass>> factory = document =>
            {
                var result = Restore(document);
                constructed.Add(result);
                return result;
            };
            var mapper = new BsonMapper();
            // Exercise the reporter's existing API until an explicit opt-in exists.
            // Merely lacking that new API must not be this test's failure.
            if (Issue2873_Tests.ConfigureConstructorOnly(mapper, factory) == null)
            {
                mapper.Entity<QuantityRange<Mass>>().Ctor(factory);
            }

            Exception failure;
            QuantityRange<Mass>[] actual = null;
            using (var reader = new LiteDatabase(file.Filename, mapper))
            {
                AssertRawLedger(reader, keys, receipts);
                failure = Record.Exception(() =>
                    actual = reader.GetCollection<QuantityRange<Mass>>("ranges").FindAll().ToArray());
                AssertRawLedger(reader, keys, receipts);
            }
            using (var reopened = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                AssertRawLedger(reopened, keys, receipts);
            }

            // On unfixed dev the first factory succeeds and returns the exact Enum
            // instance, then DeserializeObject tries to overwrite it with a string.
            constructed.Should().NotBeEmpty("the custom factory must actually run");
            foreach (var result in constructed)
            {
                var receipt = receipts.Single(row => row.Min == result.Min);
                AssertRange(result, receipt);
            }
            failure.Should().BeNull("the reported model must not fail with String-to-Enum InvalidCastException after its factory succeeds");
            actual.Should().HaveCount(receipts.Length);
            constructed.Should().HaveCount(receipts.Length);
            foreach (var receipt in receipts)
            {
                var result = actual.Single(row => row.Min == receipt.Min);
                AssertRange(result, receipt);
                result.Should().BeSameAs(constructed.Single(row => row.Min == receipt.Min));
            }
        }

        private static void AssertRawLedger(LiteDatabase database, List<BsonValue> keys, QuantityRange<Mass>[] receipts)
        {
            var rows = database.GetCollection("ranges");
            rows.FindAll().Select(row => row["_id"]).Should().BeEquivalentTo(keys);
            for (var index = 0; index < receipts.Length; index++)
            {
                var raw = rows.FindById(keys[index]);
                raw["Min"].AsDouble.Should().Be(receipts[index].Min);
                raw["Max"].AsDouble.Should().Be(receipts[index].Max);
                raw["Unit"].AsString.Should().Be(receipts[index].Unit.ToString());
                // Independent direct invocation proves the reporter's JSON-based
                // factory itself understands the actual persisted representation.
                AssertRange(Restore(raw), receipts[index]);
            }
        }

        private static QuantityRange<Mass> Restore(BsonDocument document)
        {
            using var json = JsonDocument.Parse(document.ToString());
            var root = json.RootElement;
            return new QuantityRange<Mass>(
                root.GetProperty("Min").GetDouble(),
                root.GetProperty("Max").GetDouble(),
                Enum.Parse<Mass.Units>(root.GetProperty("Unit").GetString()));
        }

        private static void AssertRange(QuantityRange<Mass> actual, QuantityRange<Mass> expected)
        {
            actual.Min.Should().Be(expected.Min);
            actual.Max.Should().Be(expected.Max);
            actual.Unit.Should().BeOfType<Mass.Units>().Which.Should().Be((Mass.Units)expected.Unit);
        }

        // Preserve the init-only members and System.Enum-typed property from #2298,
        // the concrete deserialization report consolidated into #2873.
        public struct Mass
        {
            public enum Units { Pound, Kilogram }
            public Mass(double value, Units unit) { Value = value; Unit = unit; }
            public double Value { get; init; }
            public Units Unit { get; init; }
        }

        public class QuantityRange<T>
        {
            public QuantityRange(double min, double max, Enum unit) { Min = min; Max = max; Unit = unit; }
            public double Min { get; init; }
            public double Max { get; init; }
            public Enum Unit { get; init; }
        }
    }
}
#endif
