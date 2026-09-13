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
