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

        /// <summary>
        /// Default-initialized enum properties must remain valid persisted dictionary keys.
        /// </summary>
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

        /// <summary>
        /// Nullable key declarations must preserve the same unnamed values as non-nullable enums.
        /// </summary>
        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Nullable_enum_key_can_be_saved_reopened_and_updated(string culture)
        {
            AssertRoundTripAndUpdate(culture, new Dictionary<OrderStatus?, int>
            {
                [new Order().Status] = 1,
                [OrderStatus.Pending] = 2
            });
        }

        /// <summary>
        /// HTTP extension codes need not have a name in the framework enum to be persisted.
        /// </summary>
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

        /// <summary>
        /// Named enum keys retain their values through persistence and an unrelated update.
        /// </summary>
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

        /// <summary>
        /// Numeric dictionary keys must use culture-independent formatting without losing entries.
        /// </summary>
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

        /// <summary>
        /// Numeric-looking string keys must remain distinct literal strings.
        /// </summary>
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

        /// <summary>
        /// Documents written before PR #2753 must support updates without a data migration.
        /// </summary>
        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Existing_enum_dictionary_allows_updating_an_unrelated_field(string culture)
        {
            AssertExistingDictionaryUpdate(culture, new Dictionary<OrderStatus, int>
            {
                [default(OrderStatus)] = 1,
                [OrderStatus.Pending] = 2
            });
        }

        /// <summary>
        /// The old fixture also supports applications declaring their enum dictionary keys nullable.
        /// </summary>
        [Theory]
        [InlineData("de-AT")]
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Existing_nullable_enum_dictionary_allows_updating_an_unrelated_field(string culture)
        {
            AssertExistingDictionaryUpdate(culture, new Dictionary<OrderStatus?, int>
            {
                [default(OrderStatus)] = 1,
                [OrderStatus.Pending] = 2
            });
        }

        /// <summary>
        /// Reads the unchanged legacy fixture and verifies that editing only its name preserves keys.
        /// </summary>
        private static void AssertExistingDictionaryUpdate<TKey>(string culture, Dictionary<TKey, int> expected)
        {
            var previousCulture = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                var original = Path.Combine(AppContext.BaseDirectory, "Resources", "Issue2255_Before2753.db");
                using var file = new TempFile(original);

                // This is an actual database created in de-AT by the pre-PR library.
                using (var db = new LiteDatabase(file.Filename))
                {
                    Assert.Equal("de-AT", db.Collation.Culture.Name);
                    var collection = db.GetCollection<Summary<TKey>>("summaries");
                    var loaded = collection.FindById(1);
                    Assert.Equal("original", loaded.Name);
                    AssertCounts(expected, loaded.Counts);

                    loaded.Name = "updated";
                    Assert.True(collection.Update(loaded));
                }

                using (var db = new LiteDatabase(file.Filename))
                {
                    var loaded = db.GetCollection<Summary<TKey>>("summaries").FindById(1);
                    Assert.Equal("updated", loaded.Name);
                    AssertCounts(expected, loaded.Counts);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }
        }

        /// <summary>
        /// Exercises file creation, reopening, updating, and reopening again through the public API.
        /// </summary>
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

        /// <summary>
        /// Verifies dictionary identity by key and value, independently of enumeration order.
        /// </summary>
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
