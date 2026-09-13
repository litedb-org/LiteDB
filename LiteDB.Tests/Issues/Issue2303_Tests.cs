using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2303_Tests
    {
        public class Point
        {
            private double _x;
            private double _y;
            private string _label;
            [BsonCtor]
            public Point(double x, double y) { _x = x; _y = y; ConstructorCalls++; }
            public double X { get => _x; set { _x = value; CoordinateSets++; } }
            public double Y { get => _y; set { _y = value; CoordinateSets++; } }
            public string Label { get => _label; set { _label = value; LabelSets++; } }
            [BsonIgnore] public int ConstructorCalls { get; private set; }
            [BsonIgnore] public int CoordinateSets { get; private set; }
            [BsonIgnore] public int LabelSets { get; private set; }
        }

        [Fact]
        public void Constructor_bound_properties_are_not_set_again_but_unbound_properties_are()
        {
            var mapper = new BsonMapper();
            var raw = new BsonDocument { ["X"] = 1.25, ["Y"] = -3.5, ["Label"] = "unbound" };
            var point = mapper.ToObject<Point>(raw);
            point.X.Should().Be(1.25);
            point.Y.Should().Be(-3.5);
            point.Label.Should().Be("unbound");
            point.ConstructorCalls.Should().Be(1);
            point.CoordinateSets.Should().Be(0, "the constructor already initialized those properties");
            point.LabelSets.Should().Be(1, "skipping every setter would lose properties not bound to constructor parameters");
        }
    }
}
