using System;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2414_Tests
    {
        [Theory]
        [InlineData("2018-05-01T15:30:45Z")]
        [InlineData("2018-12-31T23:30:00Z")]
        [InlineData("2018-01-01T00:30:00Z")]
        public void Date_parts_use_local_calendar_after_JSON_conversion_without_changing_the_instant(string iso)
        {
            var utc = DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
            var document = JsonSerializer.Deserialize("{mydate:{$date:'" + iso + "'}}").AsDocument;
            document["mydate"].AsDateTime.ToUniversalTime().Should().Be(utc);
            BsonExpression.Create("YEAR(mydate)").ExecuteScalar(document).AsInt32.Should().Be(local.Year);
            BsonExpression.Create("MONTH(mydate)").ExecuteScalar(document).AsInt32.Should().Be(local.Month);
            BsonExpression.Create("DAY(mydate)").ExecuteScalar(document).AsInt32.Should().Be(local.Day);
            // UTC values remain UTC: globally forcing every date to one timezone is not a fix.
            document["mydate"] = utc;
            BsonExpression.Create("DAY(mydate)").ExecuteScalar(document).AsInt32.Should().Be(utc.Day);
        }
    }
}
