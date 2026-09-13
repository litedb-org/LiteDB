using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2298_Tests
{
    public struct Mass
    {
        public enum Units
        { Pound, Kilogram }

        public Mass(double value, Units unit)
        { Value = value; Unit = unit; }

        public double Value { get; init; }
        public Units Unit { get; init; }
    }

    public class QuantityRange<T>
    {
        public QuantityRange(double min, double max, Enum unit)
        { Min = min; Max = max; Unit = unit; }

        public double Min { get; init; }
        public double Max { get; init; }
        public Enum Unit { get; init; }
    }

    public static QuantityRange<Mass> MassRangeBuilder(BsonDocument document)
    {
        var doc = JsonDocument.Parse(document.ToString()).RootElement;
        var min = doc.GetProperty(nameof(QuantityRange<Mass>.Min)).GetDouble();
        var max = doc.GetProperty(nameof(QuantityRange<Mass>.Max)).GetDouble();
        var unit = Enum.Parse<Mass.Units>(doc.GetProperty(nameof(QuantityRange<Mass>.Unit)).GetString());

        var restored = new QuantityRange<Mass>(min, max, unit);
        return restored;
    }

    [Fact]
    public void We_Dont_Need_Ctor()
    {
        BsonMapper.Global.RegisterType<QuantityRange<Mass>>(
            serialize: (range) => new BsonDocument
            {
                { nameof(QuantityRange<Mass>.Min), range.Min },
                { nameof(QuantityRange<Mass>.Max), range.Max },
                { nameof(QuantityRange<Mass>.Unit), range.Unit.ToString() }
            },
            deserialize: (document) => MassRangeBuilder(document as BsonDocument)
        );

        var range = new QuantityRange<Mass>(100, 500, Mass.Units.Pound);
        var filename = "Demo.DB";
        var DB = new LiteDatabase(filename);
        var collection = DB.GetCollection<QuantityRange<Mass>>("DEMO");
        collection.Insert(range);
        var restored = collection.FindAll().First();
    }
}