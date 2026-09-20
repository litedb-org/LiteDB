using System;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Mapper
{
    public class LinqCacheWideFuzz_Tests
    {
        [Fact]
        public void Exploratory_wide_cache_campaign()
        {
            var shapesText = Environment.GetEnvironmentVariable("LITEDB_FUZZ_SHAPES");
            if (string.IsNullOrWhiteSpace(shapesText)) return;

            var start = Read("LITEDB_FUZZ_SEED", 0);
            var shapes = Read("LITEDB_FUZZ_SHAPES", 1500);
            if (shapes <= 0 || start > int.MaxValue - shapes)
                throw new ArgumentOutOfRangeException(nameof(shapes));
            var configuration = Read("LITEDB_FUZZ_CONFIG", 0);
            if (configuration < 0 || configuration > 3)
                throw new ArgumentOutOfRangeException(nameof(configuration));
            var threads = Read("LITEDB_FUZZ_THREADS", 8);
            if (threads == 0 || threads < -1) threads = 1;
            var output = Environment.GetEnvironmentVariable("LITEDB_FUZZ_OUTPUT");
            var summary = new LinqCacheWideFuzzCampaign(start, shapes, configuration, threads, output).Run();

            summary.Translated.Should().BeGreaterThan(shapes);
            summary.CacheFailures.Should().Be(0);
            summary.GeneratorFailures.Should().Be(0);
            var translatorBudget = Environment.GetEnvironmentVariable("LITEDB_FUZZ_MAX_TRANSLATOR_DIFFS");
            if (!string.IsNullOrWhiteSpace(translatorBudget))
            {
                if (!int.TryParse(translatorBudget, out var maxTranslatorDifferences) || maxTranslatorDifferences < 0)
                    throw new ArgumentOutOfRangeException(nameof(translatorBudget));
                summary.TranslatorDifferences.Should().BeLessOrEqualTo(maxTranslatorDifferences);
            }
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(419, 2)]
        [InlineData(199999, 1)]
        public void Wide_generator_is_replayable(int shapeSeed, int valueSeed)
        {
            var first = new LinqCacheWideFuzzGenerator(shapeSeed, valueSeed);
            var second = new LinqCacheWideFuzzGenerator(shapeSeed, valueSeed);
            var row = new WideFuzzRow
            {
                Value = 7, Optional = 3, Name = "Ready", Items = new[] { 1, 7, 12 },
                When = new DateTime(2024, 3, 31, 1, 30, 0, DateTimeKind.Local),
                Next = new WideFuzzRow { Value = 8, Name = "next", Items = new[] { 2 }, When = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc) }
            };
            var mapper = new BsonMapper();

            for (var kind = 0; kind < LinqCacheWideFuzzGenerator.Kinds; kind++)
            {
                var firstQuery = first.Build(kind);
                var secondQuery = second.Build(kind);
                firstQuery.ToString().Should().Be(secondQuery.ToString());
                var firstValue = firstQuery.Compile().DynamicInvoke(row);
                var secondValue = secondQuery.Compile().DynamicInvoke(row);
                mapper.Serialize(firstQuery.ReturnType, firstValue).ToString().Should()
                    .Be(mapper.Serialize(secondQuery.ReturnType, secondValue).ToString());
            }
        }

        private static int Read(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
    }
}
