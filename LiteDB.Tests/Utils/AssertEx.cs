using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;

namespace LiteDB.Tests
{
    /// <summary>
    /// Extension and additional methods for debug
    /// </summary>
    public static class AssertEx
    {
        [DebuggerHidden]
        public static void ArrayEqual<T>(T[] first, T[] second, bool sort)
            where T : IEqualityComparer<T>
        {
            first.Length.Should().Be(second.Length, "because arrays with diferent item count");

            if (sort)
            {
                first = first.OrderBy(x => x).ToArray();
                second = second.OrderBy(x => x).ToArray();
            }

            var index = 0;

            foreach (var zip in first.Zip(second, (First, Second) => new {First, Second}))
            {
                var r = zip.First.Equals(zip.First, zip.Second);

                r.Should().BeTrue($"Index [{index}]: values are not same: `{zip.First}` != `{zip.Second}`");

                index++;
            }
        }

        //[DebuggerHidden]
        public static void ExpectValue(this BsonValue value, BsonValue expect)
        {
            value.Should().Be(expect);
        }

        //[DebuggerHidden]
        public static void ExpectValue<T>(this T value, T expect)
        {
            value.Should().Be(expect);
        }

        //[DebuggerHidden]
        public static void ExpectArray(this BsonValue value, params BsonValue[] args)
        {
            value.Should().Be(new BsonArray(args));
        }

        //[DebuggerHidden]
        public static void ExpectJson(this BsonValue value, string expectJson)
        {
            value.Should().Be((JsonSerializer.Deserialize(expectJson)));
        }

        //[DebuggerHidden]
        public static void ExpectValues(this IEnumerable<BsonValue> values, params BsonValue[] expectValues)
        {
            values.ToArray().Should().Equal(expectValues);
        }

        [DebuggerHidden]
        public static void ExpectValues<T>(this IEnumerable<T> values, params T[] expectValues)
        {
            values.ToArray().Should().Equal(expectValues);
        }

        [DebuggerHidden]
        public static void ExpectCount<T>(this IEnumerable<T> values, int count)
        {
            count.Should().Be(count);
        }
    }
}