using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2324_Tests
    {
        public class Row { public int Key { get; set; } public string Value { get; set; } }

        [Fact]
        public void Reapplying_id_keeps_its_name_and_does_not_rerun_the_field_resolver()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Row>().Id(x => x.Key, false);
            mapper.ResolveFieldName = name => throw new InvalidOperationException("resolver must not run for the existing ID");
            mapper.Entity<Row>().Id(x => x.Key, true);
            mapper.Entity<Row>().Id(x => x.Key, false);
            var raw = mapper.ToDocument(new Row { Key = 42, Value = "unchanged" });
            raw["_id"].AsInt32.Should().Be(42);
            raw.ContainsKey("Key").Should().BeFalse();
            mapper.ToObject<Row>(raw).Key.Should().Be(42);
        }

        [Fact]
        public void Selecting_another_id_restores_the_previous_members_resolved_name()
        {
            var mapper = new BsonMapper { ResolveFieldName = name => "mapped_" + name };
            mapper.Entity<Row>().Id(x => x.Key, false);
            mapper.Entity<Row>().Id(x => x.Value, false);
            var raw = mapper.ToDocument(new Row { Key = 42, Value = "new ID" });
            raw["mapped_Key"].AsInt32.Should().Be(42);
            raw["_id"].AsString.Should().Be("new ID");
            raw.ContainsKey("mapped_Value").Should().BeFalse();
            var restored = mapper.ToObject<Row>(raw);
            restored.Key.Should().Be(42);
            restored.Value.Should().Be("new ID");
        }

        [Fact]
        public void Reapplying_same_id_mapping_during_serialization_never_exposes_partial_mapping()
        {
            var mapper = new BsonMapper();
            mapper.Entity<Row>().Id(x => x.Key, false);
            using var start = new Barrier(2);
            var configure = Task.Run(() =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                for (var i = 0; i < 50000; i++) mapper.Entity<Row>().Id(x => x.Key, false);
            });
            start.SignalAndWait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            try
            {
                for (var i = 1; i <= 50000; i++)
                {
                    var raw = mapper.ToDocument(new Row { Key = i, Value = "payload " + i });
                    raw["_id"].Should().Be(new BsonValue(i));
                    raw.ContainsKey("Key").Should().BeFalse();
                    raw["Value"].AsString.Should().Be("payload " + i);
                }
            }
            finally { configure.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(); }
            var decoded = mapper.ToObject<Row>(new BsonDocument { ["_id"] = 70001, ["Value"] = "independent BSON" });
            decoded.Key.Should().Be(70001);
            decoded.Value.Should().Be("independent BSON");
        }
    }
}
