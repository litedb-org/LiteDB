using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    [TypeConverter(typeof(DeclaredKeyConverter))]
    public abstract class DeclaredKey
    {
        protected DeclaredKey(int number)
        {
            Number = number;
        }

        public int Number { get; }
    }

    [TypeConverter(typeof(RuntimeKeyConverter))]
    public sealed class FirstKey : DeclaredKey
    {
        public FirstKey(int number) : base(number)
        {
        }
    }

    [TypeConverter(typeof(RuntimeKeyConverter))]
    public sealed class SecondKey : DeclaredKey
    {
        public SecondKey(int number) : base(number)
        {
        }
    }

    public sealed class DeclaredKeyConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        public override object ConvertTo(
            ITypeDescriptorContext context,
            CultureInfo culture,
            object value,
            Type destinationType)
        {
            if (destinationType == typeof(string) && value is DeclaredKey key)
            {
                var kind = key is FirstKey ? "first" : "second";
                return $"{kind}:{key.Number}";
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
    }

    public sealed class RuntimeKeyConverter : TypeConverter
    {
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        public override object ConvertTo(
            ITypeDescriptorContext context,
            CultureInfo culture,
            object value,
            Type destinationType)
        {
            if (destinationType == typeof(string) && value is DeclaredKey key)
            {
                // Both runtime types deliberately use the same text. The declared
                // converter above is what makes their persisted keys unambiguous.
                return key.Number.ToString(CultureInfo.InvariantCulture);
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
    }

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

    [Fact]
    public void Dictionary_keys_should_use_the_declared_key_converter()
    {
        var values = new Dictionary<DeclaredKey, string>
        {
            [new FirstKey(42)] = "first value",
            [new SecondKey(42)] = "second value"
        };

        var document = new BsonMapper().Serialize(values).AsDocument;

        Assert.Equal(2, document.Count);
        Assert.Equal("first value", document["first:42"].AsString);
        Assert.Equal("second value", document["second:42"].AsString);
    }

    [Fact]
    public void Resaving_a_dictionary_should_keep_its_existing_field_name()
    {
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            var key = new DateTimeOffset(2026, 8, 10, 15, 27, 33, TimeSpan.FromHours(2));
            var fieldNameWrittenByPreviousVersions = key.ToString(CultureInfo.CurrentCulture);
            var documentFromExistingDatabase = new BsonDocument
            {
                [fieldNameWrittenByPreviousVersions] = "saved before upgrade"
            };

            var mapper = new BsonMapper();
            var values = mapper.Deserialize<Dictionary<DateTimeOffset, string>>(documentFromExistingDatabase);
            var rewrittenDocument = mapper.Serialize(values).AsDocument;

            Assert.True(
                rewrittenDocument.ContainsKey(fieldNameWrittenByPreviousVersions),
                $"Expected the existing field name '{fieldNameWrittenByPreviousVersions}' to be preserved.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
