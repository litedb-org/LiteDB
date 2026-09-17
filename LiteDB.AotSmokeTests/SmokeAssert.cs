using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using LiteDB.Generated;

#nullable enable
namespace LiteDB.AotSmokeTests
{
    internal static class SmokeAssert
    {
        internal static void RunScenario(string name, Action scenario)
        {
            Console.WriteLine($"[SCENARIO] {name}");
            scenario();
            Console.WriteLine($"[PASS] {name}");
            Console.WriteLine();
        }

        internal static void RequireCanonicalDateTimeOffsetValue(BsonValue value, DateTimeOffset expected)
        {
            Require(value.IsDateTime &&
                    value.AsDateTime.ToUniversalTime().Ticks == GetCanonicalDateTimeOffsetTicks(expected),
                "The source-generated Native AOT DateTimeOffset BSON DateTime value did not match ordinary mapping.");
        }

        internal static bool IsCanonicalDateTimeOffset(DateTimeOffset expected, DateTimeOffset actual)
        {
            return actual.Offset == TimeSpan.Zero &&
                actual.UtcTicks == GetCanonicalDateTimeOffsetTicks(expected);
        }

        private static long GetCanonicalDateTimeOffsetTicks(DateTimeOffset value) =>
            value == DateTimeOffset.MinValue || value == DateTimeOffset.MaxValue
                ? DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified).ToUniversalTime().Ticks
                : value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerMillisecond);

        internal static void RequireThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        internal static void RequireNativeScalar(string field, bool condition, string expected, string actual)
        {
            Require(condition,
                $"The source-generated Native AOT native scalar round trip failed for '{field}'. Expected: '{expected}'. Actual: '{actual}'.");
            Console.WriteLine($"        Passed: {field}.");
        }

        internal static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
