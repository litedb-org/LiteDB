using System;
using System.Linq.Expressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqCacheFuzz_Tests
    {
        // CI runs a fixed, reproducible range inside its five-minute test budget.
        // Set LITEDB_FUZZ_SHAPES and LITEDB_FUZZ_SEED to explore other ranges locally.
        private static readonly int Shapes = Setting("LITEDB_FUZZ_SHAPES", 400);
        private static readonly int FirstSeed = Setting("LITEDB_FUZZ_SEED", 0);
        private const int ValueSeeds = 3;

        private static readonly BsonDocument Document = new BsonDocument
        {
            ["Value"] = 7, ["Name"] = "Ready", ["State"] = "Ready",
            ["Next"] = new BsonDocument { ["Value"] = 3 },
            ["Tags"] = new BsonDocument { ["ak"] = 7, ["Readyk"] = 1 },
            ["When"] = new DateTime(2024, 3, 10, 7, 30, 0, DateTimeKind.Utc), ["Optional"] = 9,
            ["Numbers"] = new BsonArray(1, 3, 7), ["Contract"] = new BsonDocument { ["Value"] = 11 }
        };

        // One mapper serves every seed of a run, so unrelated shapes share buckets,
        // evict each other and are revisited; each translation must still equal what
        // an uncached translator produces for the same tree.
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Cached_translation_equals_direct_translation_for_random_shapes(bool enumAsInteger, bool reverse)
        {
            var mapper = new BsonMapper { EnumAsInteger = enumAsInteger };
            var translated = 0;
            for (var i = 0; i < Shapes; i++)
            {
                var shapeSeed = FirstSeed + (reverse ? Shapes - 1 - i : i);
                for (var valueSeed = 0; valueSeed < ValueSeeds; valueSeed++)
                {
                    var query = new LinqCacheFuzzGenerator(shapeSeed, valueSeed).Build(Math.Abs(shapeSeed % LinqCacheFuzzGenerator.Kinds));
                    if (AssertParity(mapper, query, enumAsInteger, $"shape seed {shapeSeed}, value seed {valueSeed}: {query}")) translated++;
                }
            }
            // Guard the generator: a grammar the translator mostly rejects proves nothing.
            translated.Should().BeGreaterThan(Shapes * ValueSeeds / 2);
            mapper.LinqExpressionCacheCount.Should().BeGreaterThan(0);
        }

        private static bool AssertParity(BsonMapper mapper, LambdaExpression query, bool enumAsInteger, string context)
        {
            var actual = Capture(() => Translate(mapper, query), out var actualError);
            BsonExpression expected;
            Exception expectedError;
            using (new DirectTranslationScope())
                expected = Capture(() => Translate(new BsonMapper { EnumAsInteger = enumAsInteger }, query), out expectedError);

            using (new AssertionScope(context))
            {
                (actualError?.GetType()).Should().Be(expectedError?.GetType());
                if (actualError != null || expectedError != null) return false;
                ExpressionParity.AssertMetadata(actual, expected);
                actual.Parameters.ToString().Should().Be(expected.Parameters.ToString());
                var actualValue = Capture(() => actual.ExecuteScalar(Document, Collation.Binary), out var actualRun);
                var expectedValue = Capture(() => expected.ExecuteScalar(Document, Collation.Binary), out var expectedRun);
                (actualRun?.GetType()).Should().Be(expectedRun?.GetType());
                if (actualRun == null && expectedRun == null) actualValue.Should().Be(expectedValue);
            }
            return true;
        }

        private static BsonExpression Translate(BsonMapper mapper, LambdaExpression query)
        {
            switch (query)
            {
                case Expression<Func<FuzzRow, bool>> predicate: return mapper.GetExpression(predicate);
                case Expression<Func<FuzzRow, int>> scalar: return mapper.GetExpression(scalar);
                case Expression<Func<FuzzRow, object[]>> array: return mapper.GetExpression(array);
                default: return mapper.GetExpression((Expression<Func<FuzzRow, FuzzRow>>)query);
            }
        }

        private static int Setting(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

        private static T Capture<T>(Func<T> action, out Exception error)
        {
            error = null;
            try { return action(); }
            catch (Exception exception)
            {
                error = exception;
                return default(T);
            }
        }
    }
}
