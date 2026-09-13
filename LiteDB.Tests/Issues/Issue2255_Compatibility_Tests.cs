using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2255_Compatibility_Tests
    {
        public enum OrderStatus
        {
            Pending = 1,
            Completed = 2
        }

        public class Order
        {
            public OrderStatus Status { get; set; }
        }

        public class Summary<TKey>
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public Dictionary<TKey, int> Counts { get; set; }
        }

        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Default_enum_key_can_be_saved_reopened_and_updated(string culture)
        {
            // An ordinary uninitialized property is zero even when the enum starts at one.
            var order = new Order();
            AssertRoundTripAndUpdate(culture, new Dictionary<OrderStatus, int>
            {
                [order.Status] = 1,
                [OrderStatus.Pending] = 2
            });
        }

        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Unnamed_http_status_key_can_be_saved_reopened_and_updated(string culture)
        {
            // HTTP responses can contain extension codes absent from the framework enum.
            AssertRoundTripAndUpdate(culture, new Dictionary<HttpStatusCode, int>
            {
                [HttpStatusCode.OK] = 10,
                [(HttpStatusCode)599] = 1
            });
        }

        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Named_enum_keys_can_be_saved_reopened_and_updated(string culture)
        {
            AssertRoundTripAndUpdate(culture, new Dictionary<OrderStatus, int>
            {
                [OrderStatus.Pending] = 1,
                [OrderStatus.Completed] = 2
            });
        }

        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Decimal_keys_can_be_saved_reopened_and_updated(string culture)
        {
            AssertRoundTripAndUpdate(culture, new Dictionary<decimal, int>
            {
                [9.9m] = 1,
                [99m] = 2
            });
        }

        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Comma_and_dot_string_keys_remain_distinct_after_reopening_and_updating(string culture)
        {
            AssertRoundTripAndUpdate(culture, new Dictionary<string, int>
            {
                ["9.9"] = 1,
                ["9,9"] = 2
            });
        }

        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Existing_enum_dictionary_allows_updating_an_unrelated_field(string culture)
        {
            var previousCulture = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var original = Path.Combine(AppContext.BaseDirectory, "Resources", "Issue2255_Before2753.db");
                using var file = new TempFile(original);
                var expected = new Dictionary<OrderStatus, int>
                {
                    [default(OrderStatus)] = 1,
                    [OrderStatus.Pending] = 2
                };

                // This is an actual database created in de-AT by the pre-PR library.
                using (var db = new LiteDatabase(file.Filename))
                {
                    Assert.Equal("de-AT", db.Collation.Culture.Name);
                    var collection = db.GetCollection<Summary<OrderStatus>>("summaries");
                    var loaded = collection.FindById(1);
                    Assert.Equal("original", loaded.Name);
                    AssertCounts(expected, loaded.Counts);

                    loaded.Name = "updated";
                    Assert.True(collection.Update(loaded));
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    var loaded = db.GetCollection<Summary<OrderStatus>>("summaries").FindById(1);
                    Assert.Equal("updated", loaded.Name);
                    AssertCounts(expected, loaded.Counts);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static void AssertRoundTripAndUpdate<TKey>(string culture, Dictionary<TKey, int> counts)
        {
            var previousCulture = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var file = new TempFile();

                using (var db = new LiteDatabase(file.Filename))
                {
                    db.GetCollection<Summary<TKey>>("summaries").Insert(new Summary<TKey>
                    {
                        Id = 1,
                        Name = "original",
                        Counts = counts
                    });
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    var collection = db.GetCollection<Summary<TKey>>("summaries");
                    var loaded = collection.FindById(1);
                    AssertCounts(counts, loaded.Counts);

                    loaded.Name = "updated";
                    Assert.True(collection.Update(loaded));
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    var loaded = db.GetCollection<Summary<TKey>>("summaries").FindById(1);
                    Assert.Equal("updated", loaded.Name);
                    AssertCounts(counts, loaded.Counts);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        private static void AssertCounts<TKey>(Dictionary<TKey, int> expected, Dictionary<TKey, int> actual)
        {
            Assert.Equal(expected.Count, actual.Count);

            foreach (var pair in expected)
            {
                Assert.True(actual.TryGetValue(pair.Key, out var value), $"Missing key: {pair.Key}");
                Assert.Equal(pair.Value, value);
            }
        }
    }
}
