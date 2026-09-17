using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2847StringSearch_Tests
    {
        public class Row { public int Id { get; set; } public string Name { get; set; } }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Search_signatures_match_CLR_for_every_mode_and_enum_encoding(bool enumAsInteger, bool indexed)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                var mapper = new BsonMapper { EnumAsInteger = enumAsInteger };
                using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = Collation.Binary }, mapper);
                var source = new[] { "iiIİıi", "IIiıİI", "i%_xxi", "otherx" }
                    .Select((text, i) => new Row { Id = i + 1, Name = text }).ToArray();
                var rows = db.GetCollection<Row>();
                rows.Insert(source);
                if (indexed) rows.EnsureIndex(row => row.Name);
                foreach (StringComparison mode in Enum.GetValues(typeof(StringComparison)))
                {
                    AssertQuery(rows, source, row => row.Name.StartsWith("i", mode));
                    AssertQuery(rows, source, row => row.Name.EndsWith("i", mode));
                    AssertQuery(rows, source, row => row.Name.IndexOf("i", mode) == 0);
                    AssertQuery(rows, source, row => row.Name.IndexOf("i", 1, mode) == 2);
                    AssertQuery(rows, source, row => row.Name.IndexOf("i", 1, 3, mode) == 2);
#if !NETFRAMEWORK
                    AssertQuery(rows, source, row => row.Name.Contains("I", mode));
                    AssertQuery(rows, source, row => row.Name.Contains("%_", mode));
#endif
                    AssertQuery(rows, source, row => row.Name.StartsWith("i%_", mode));
                    AssertQuery(rows, source, row => row.Name.EndsWith("", mode));
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Theory]
        [InlineData("STRING_STARTSWITH")]
        [InlineData("STRING_ENDSWITH")]
        [InlineData("STRING_CONTAINS")]
        [InlineData("STRING_INDEXOF")]
        public void Explicit_search_preserves_null_errors_and_cannot_be_persistently_indexed(string function)
        {
            Action receiver = () => BsonExpression.Create(function + "(null, 'x', 'Ordinal')").ExecuteScalar();
            receiver.Should().Throw<NullReferenceException>();
            Action argument = () => BsonExpression.Create(function + "('x', null, 'Ordinal')").ExecuteScalar();
            argument.Should().Throw<ArgumentNullException>();
            Action mode = () => BsonExpression.Create(function + "('x', 'x', 99)").ExecuteScalar();
            mode.Should().Throw<ArgumentException>();
            using var db = new LiteDatabase(":memory:");
            Action index = () => db.GetCollection<Row>().EnsureIndex("search", function + "($.Name, 'x', 'CurrentCulture')");
            index.Should().Throw<ArgumentException>().WithMessage("*immutable*");
        }

        [Fact]
        public void Explicit_IndexOf_validates_its_start_and_count()
        {
            var mapper = new BsonMapper();
            var doc = mapper.ToDocument(new Row { Name = "abc" });
            Action start = () => mapper.GetExpression<Row, int>(row => row.Name.IndexOf("a", -1, StringComparison.Ordinal)).ExecuteScalar(doc);
            Action count = () => mapper.GetExpression<Row, int>(row => row.Name.IndexOf("a", 1, 9, StringComparison.Ordinal)).ExecuteScalar(doc);
            start.Should().Throw<ArgumentOutOfRangeException>();
            count.Should().Throw<ArgumentOutOfRangeException>();
        }

        private static void AssertQuery(ILiteCollection<Row> rows, Row[] source, Expression<Func<Row, bool>> predicate)
        {
            rows.Find(predicate).Select(row => row.Id).OrderBy(id => id)
                .Should().Equal(source.Where(predicate.Compile()).Select(row => row.Id));
        }
    }
}
