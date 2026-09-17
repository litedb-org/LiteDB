using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2847ComparisonModes_Tests
    {
        public class Row { public int Id { get; set; } public string Name { get; set; } }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void All_explicit_modes_match_CLR_with_both_enum_encodings_and_index_presence(bool enumAsInteger, bool indexed)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                var mapper = new BsonMapper { EnumAsInteger = enumAsInteger };
                using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary }, mapper);
                var rows = new[]
                {
                    new Row { Id = 1, Name = "i" }, new Row { Id = 2, Name = "I" },
                    new Row { Id = 3, Name = "İ" }, new Row { Id = 4, Name = "ı" },
                    new Row { Id = 5, Name = "other" }
                };
                var collection = db.GetCollection<Row>();
                collection.Insert(rows);
                if (indexed) collection.EnsureIndex(row => row.Name);
                foreach (StringComparison mode in Enum.GetValues(typeof(StringComparison)))
                {
                    Expression<Func<Row, bool>> instance = row => row.Name.Equals("i", mode);
                    Expression<Func<Row, bool>> staticCall = row => string.Equals(row.Name, "i", mode);
                    var expected = rows.Where(instance.Compile()).Select(row => row.Id);
                    collection.Find(instance).Select(row => row.Id).OrderBy(id => id).Should().Equal(expected);
                    collection.Find(staticCall).Select(row => row.Id).OrderBy(id => id).Should().Equal(expected);
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Fact]
        public void Static_no_mode_equality_uses_its_operands_and_explicit_modes_cannot_be_indexed()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary });
            var rows = db.GetCollection<Row>();
            rows.Insert(new[] { new Row { Id = 1, Name = "one" }, new Row { Id = 2, Name = "two" } });
            rows.Find(row => string.Equals(row.Name, "two")).Select(row => row.Id).Should().Equal(2);
            Action index = () => rows.EnsureIndex("comparison", "STRING_EQUALS($.Name, 'one', 'CurrentCulture')");
            index.Should().Throw<ArgumentException>().WithMessage("*immutable*");
            rows.Count().Should().Be(2);
        }

        [Fact]
        public void Static_null_equality_and_instance_null_failure_follow_CLR()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.Insert(new[] { new Row { Id = 1 }, new Row { Id = 2, Name = "x" } });
            rows.Find(row => string.Equals(row.Name, null, StringComparison.Ordinal)).Select(row => row.Id).Should().Equal(1);
            Action query = () => rows.Find(row => row.Name.Equals(null, StringComparison.Ordinal)).ToArray();
            query.Should().Throw<NullReferenceException>();
            rows.Count().Should().Be(2);
        }

        [Theory]
        [InlineData(StringComparison.Ordinal)]
        [InlineData(StringComparison.OrdinalIgnoreCase)]
        public void Prefix_and_suffix_comparisons_honor_the_explicit_mode(StringComparison mode)
        {
            var source = new[]
            {
                new Row { Id = 1, Name = "Alpha" },
                new Row { Id = 2, Name = "alpha" },
                new Row { Id = 3, Name = "ALPHA" },
                new Row { Id = 4, Name = "beta" }
            };
            using var db = new LiteDatabase(new ConnectionString
            {
                Filename = ":memory:",
                Collation = new Collation("en-US/IgnoreCase")
            });
            var rows = db.GetCollection<Row>();
            rows.Insert(source);

            Expression<Func<Row, bool>> starts = row => row.Name.StartsWith("alpha", mode);
            Expression<Func<Row, bool>> ends = row => row.Name.EndsWith("ALPHA", mode);

            using var scope = new AssertionScope();
            rows.Find(starts).Select(row => row.Id).OrderBy(id => id).Should()
                .Equal(source.Where(starts.Compile()).Select(row => row.Id));
            rows.Find(ends).Select(row => row.Id).OrderBy(id => id).Should()
                .Equal(source.Where(ends.Compile()).Select(row => row.Id));
        }

        [Theory]
        [InlineData(StringComparison.Ordinal)]
        [InlineData(StringComparison.OrdinalIgnoreCase)]
        public void IndexOf_honors_StringComparison_instead_of_treating_it_as_a_start_index(StringComparison mode)
        {
            var source = new[]
            {
                new Row { Id = 1, Name = "Alpha" },
                new Row { Id = 2, Name = "xxalpha" },
                new Row { Id = 3, Name = "xxALPHA" },
                new Row { Id = 4, Name = "none" }
            };
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.Insert(source);
            Expression<Func<Row, bool>> predicate = row => row.Name.IndexOf("alpha", mode) >= 0;

            rows.Find(predicate).Select(row => row.Id).OrderBy(id => id).Should()
                .Equal(source.Where(predicate.Compile()).Select(row => row.Id));
        }

#if !NETFRAMEWORK
        [Theory]
        [InlineData(StringComparison.Ordinal)]
        [InlineData(StringComparison.OrdinalIgnoreCase)]
        public void Contains_honors_the_explicit_mode(StringComparison mode)
        {
            var source = new[]
            {
                new Row { Id = 1, Name = "xxAlpha" },
                new Row { Id = 2, Name = "xxalpha" },
                new Row { Id = 3, Name = "none" }
            };
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.Insert(source);
            Expression<Func<Row, bool>> predicate = row => row.Name.Contains("alpha", mode);

            rows.Find(predicate).Select(row => row.Id).OrderBy(id => id).Should()
                .Equal(source.Where(predicate.Compile()).Select(row => row.Id));
        }
#endif
    }
}
