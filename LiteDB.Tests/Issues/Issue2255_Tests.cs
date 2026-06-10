using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace LiteDB.Tests.Issues;

/// <summary>
/// #2255 - non-string dictionary keys (e.g. Dictionary&lt;double,double&gt;) are
/// SERIALIZED with the current culture (key.ToString() => "9,9" under de-DE) but
/// DESERIALIZED with InvariantCulture (ConvertFromInvariantString). Under a
/// comma-decimal culture this silently corrupts/loses the keys on round-trip.
/// Serialization must be culture-invariant to match deserialization.
/// </summary>
public class Issue2255_Tests
{
    private class Row
    {
        public int Id { get; set; }
        public Dictionary<double, double> Values { get; set; }
    }

    [Fact]
    public void Double_dictionary_keys_round_trip_under_comma_decimal_culture()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var mapper = new BsonMapper();

            var row = new Row
            {
                Id = 1,
                Values = new Dictionary<double, double> { [9.9] = 1.23, [10.1] = 4.56 }
            };

            var doc = mapper.ToDocument(row);

            // keys must be stored as invariant strings ("9.9"), not "9,9"
            var values = doc["Values"].AsDocument;
            Assert.True(values.ContainsKey("9.9"), "expected invariant key '9.9'");
            Assert.True(values.ContainsKey("10.1"), "expected invariant key '10.1'");

            var back = mapper.Deserialize<Row>(doc);
            Assert.Equal(2, back.Values.Count);
            Assert.Equal(1.23, back.Values[9.9]);
            Assert.Equal(4.56, back.Values[10.1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Double_dictionary_keys_round_trip_through_database_under_comma_decimal_culture()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            using var db = new LiteDatabase(new MemoryStream());
            var col = db.GetCollection<Row>("rows");

            col.Insert(new Row
            {
                Id = 1,
                Values = new Dictionary<double, double> { [9.9] = 1.23, [10.1] = 4.56 }
            });

            var loaded = col.FindById(1);
            Assert.Equal(2, loaded.Values.Count);
            Assert.Equal(1.23, loaded.Values[9.9]);
            Assert.Equal(4.56, loaded.Values[10.1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }
}
