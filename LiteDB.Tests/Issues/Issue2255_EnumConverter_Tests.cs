using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2255_EnumConverter_Tests
    {
        [TypeConverter(typeof(StatusConverter))]
        public enum CustomStatus
        {
            Pending = 1
        }

        public enum WrappedStatus
        {
            Pending = 1
        }

        public class Summary<TKey>
        {
            public int Id { get; set; }
            public Dictionary<TKey, int> Counts { get; set; }
        }

        public sealed class StatusConverter : EnumConverter
        {
            /// <summary>
            /// Supplies a distinct persisted spelling for the enum, including unnamed values.
            /// </summary>
            public StatusConverter() : base(typeof(CustomStatus))
            {
            }

            /// <summary>
            /// Writes the custom wire format instead of the default enum name or numeric text.
            /// </summary>
            public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture,
                object value, Type destinationType)
            {
                return destinationType == typeof(string)
                    ? "status:" + ((int)(CustomStatus)value).ToString(CultureInfo.InvariantCulture)
                    : base.ConvertTo(context, culture, value, destinationType);
            }

            /// <summary>
            /// Restores enum keys from the custom wire format when the database is reopened.
            /// </summary>
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
            {
                return value is string text && text.StartsWith("status:", StringComparison.Ordinal)
                    ? (CustomStatus)int.Parse(text.Substring(7), CultureInfo.InvariantCulture)
                    : base.ConvertFrom(context, culture, value);
            }
        }

        public sealed class StatusNullableConverter : NullableConverter
        {
            /// <summary>
            /// Customizes the outer nullable wrapper while leaving its underlying enum converter unchanged.
            /// </summary>
            public StatusNullableConverter(Type type) : base(type)
            {
            }

            /// <summary>
            /// Uses a distinct wire format that the built-in underlying enum converter does not produce.
            /// </summary>
            public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture,
                object value, Type destinationType)
            {
                return destinationType == typeof(string) && value is WrappedStatus status
                    ? "wrapped:" + ((int)status).ToString(CultureInfo.InvariantCulture)
                    : base.ConvertTo(context, culture, value, destinationType);
            }

            /// <summary>
            /// Restores nullable enum keys from the outer wrapper's custom format.
            /// </summary>
            public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
            {
                return value is string text && text.StartsWith("wrapped:", StringComparison.Ordinal)
                    ? (WrappedStatus)int.Parse(text.Substring(8), CultureInfo.InvariantCulture)
                    : base.ConvertFrom(context, culture, value);
            }
        }

        /// <summary>
        /// EnumConverter subclasses keep control of named and unnamed dictionary key spellings.
        /// </summary>
        [Fact]
        public void Custom_enum_converter_controls_persisted_keys()
        {
            AssertPersistedKeys(new Dictionary<CustomStatus, int>
            {
                [default(CustomStatus)] = 10,
                [CustomStatus.Pending] = 20
            }, "status:0", "status:1");
        }

        /// <summary>
        /// The default nullable wrapper must delegate to a custom underlying enum converter.
        /// </summary>
        [Fact]
        public void Nullable_enum_uses_custom_underlying_converter()
        {
            AssertPersistedKeys(new Dictionary<CustomStatus?, int>
            {
                [default(CustomStatus)] = 10,
                [CustomStatus.Pending] = 20
            }, "status:0", "status:1");
        }

        /// <summary>
        /// A custom nullable wrapper must not be bypassed just because it wraps a default enum converter.
        /// </summary>
        [Fact]
        public void Custom_nullable_converter_controls_persisted_keys()
        {
            var type = typeof(WrappedStatus?);
            var provider = TypeDescriptor.AddAttributes(type, new TypeConverterAttribute(typeof(StatusNullableConverter)));

            try
            {
                AssertPersistedKeys(new Dictionary<WrappedStatus?, int>
                {
                    [default(WrappedStatus)] = 10,
                    [WrappedStatus.Pending] = 20
                }, "wrapped:0", "wrapped:1");
            }
            finally
            {
                TypeDescriptor.RemoveProvider(provider, type);
            }
        }

        /// <summary>
        /// Checks both raw stored field names and typed reads after closing and reopening the file.
        /// </summary>
        private static void AssertPersistedKeys<TKey>(Dictionary<TKey, int> counts, string zeroKey, string namedKey)
        {
            using var file = new TempFile();

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<Summary<TKey>>("summaries").Insert(new Summary<TKey>
                {
                    Id = 1,
                    Counts = counts
                });
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                var stored = db.GetCollection("summaries").FindById(1)["Counts"].AsDocument;
                Assert.Equal(2, stored.Count);
                Assert.Equal(10, stored[zeroKey].AsInt32);
                Assert.Equal(20, stored[namedKey].AsInt32);

                var loaded = db.GetCollection<Summary<TKey>>("summaries").FindById(1);
                Assert.Equal(counts.Count, loaded.Counts.Count);

                foreach (var pair in counts)
                {
                    Assert.Equal(pair.Value, loaded.Counts[pair.Key]);
                }
            }
        }
    }
}
