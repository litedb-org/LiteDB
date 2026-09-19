using System;
using System.Globalization;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2933_Tests
    {
        private static readonly DateTime _start = new DateTime(2020, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        private static T Under<T>(string culture, Func<T> action)
        {
            var previous = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

                return action();
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private static BsonValue Evaluate(string expression)
        {
            return BsonExpression.Create(expression, new BsonValue(_start), new BsonValue(_start.AddMinutes(90))).ExecuteScalar();
        }

        [Theory]
        [InlineData("en-US", "MINUTE")]
        [InlineData("tr-TR", "MINUTE")]
        [InlineData("tr-TR", "Minute")]
        [InlineData("tr-TR", "minute")]
        [InlineData("az-Latn-AZ", "MINUTE")]
        public void Minute_date_part_does_not_depend_on_the_current_culture(string culture, string datePart)
        {
            // "MINUTE".ToLower() is "mınute" (dotless i) under Turkish casing rules and matched no date part
            var added = Under(culture, () => Evaluate($"DATEADD('{datePart}', 5, @0)"));
            var difference = Under(culture, () => Evaluate($"DATEDIFF('{datePart}', @0, @1)"));

            added.AsDateTime.ToUniversalTime().Should().Be(_start.AddMinutes(5));
            difference.AsInt32.Should().Be(90);
        }

        [Theory]
        [InlineData("tr-TR")]
        [InlineData("en-US")]
        public void Single_letter_date_parts_keep_their_case_sensitive_meaning(string culture)
        {
            Under(culture, () => Evaluate("DATEDIFF('M', @0, @1)")).AsInt32.Should().Be(0, "M is month");
            Under(culture, () => Evaluate("DATEDIFF('m', @0, @1)")).AsInt32.Should().Be(90, "m is minute");
        }
    }
}
