using System;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2847IndexSeek_Tests
    {
        public class Row { public int Id { get; set; } public string Name { get; set; } }

        private static readonly string[] _names =
        {
            "alice", "Alice", "ALICE", "i", "I", "İ", "ı", "straße", "strasse", "STRASSE", "é", "é", "bob", null
        };

        private static readonly string[] _values = { "alice", "i", "I", "strasse", "é", "zed" };

        private static Collation GetCollation(string name) => name == "binary" ? Collation.Binary : new Collation(name);

        [Theory]
        [InlineData("en-US/IgnoreCase")]
        [InlineData("en-US/None")]
        [InlineData("tr-TR/IgnoreCase")]
        [InlineData("binary")]
        public void Explicit_modes_return_the_same_rows_with_and_without_an_index_under_every_collation(string collation)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
                var source = _names.Select((name, i) => new Row { Id = i + 1, Name = name }).ToArray();
                using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = GetCollation(collation) });
                var plain = db.GetCollection<Row>("plain");
                var indexed = db.GetCollection<Row>("indexed");
                plain.Insert(source);
                indexed.Insert(source);
                indexed.EnsureIndex(row => row.Name);

                using var scope = new AssertionScope();
                foreach (StringComparison mode in Enum.GetValues(typeof(StringComparison)))
                {
                    foreach (var value in _values)
                    {
                        Expression<Func<Row, bool>> instance = row => row.Name.Equals(value, mode);
                        Expression<Func<Row, bool>> staticCall = row => string.Equals(row.Name, value, mode);
                        Expression<Func<Row, bool>> starts = row => row.Name.StartsWith(value, mode);
                        var equal = source.Where(row => string.Equals(row.Name, value, mode)).Select(row => row.Id).ToArray();
                        var prefixed = source.Where(row => row.Name != null && row.Name.StartsWith(value, mode)).Select(row => row.Id).ToArray();

                        foreach (var rows in new[] { plain, indexed })
                        {
                            var because = $"{rows.Name} {mode} '{value}'";
                            Ids(rows, instance).Should().Equal(equal, because);
                            Ids(rows, staticCall).Should().Equal(equal, because);
                            Ids(rows, starts).Should().Equal(prefixed, because);
                        }
                    }
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Theory]
        [InlineData("en-US/IgnoreCase")]
        [InlineData("en-US/None")]
        [InlineData("tr-TR/IgnoreCase")]
        [InlineData("binary")]
        public void Ordinal_equality_seeks_the_index_under_every_collation(string collation)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = GetCollation(collation) });
            var rows = db.GetCollection<Row>();
            rows.Insert(_names.Select((name, i) => new Row { Id = i + 1, Name = name }));
            rows.EnsureIndex(row => row.Name);
            var mode = StringComparison.Ordinal;

            using var scope = new AssertionScope();
            Mode(rows, row => row.Name.Equals("alice", StringComparison.Ordinal)).Should().StartWith("INDEX SEEK(Name = ");
            Mode(rows, row => row.Name.Equals("alice", mode)).Should().StartWith("INDEX SEEK(Name = ");
            Mode(rows, row => string.Equals(row.Name, "alice", mode)).Should().StartWith("INDEX SEEK(Name = ");
            Mode(rows, row => string.Equals("alice", row.Name, mode)).Should().StartWith("INDEX SEEK(Name = ");
            Mode(rows, row => row.Id > 0 && row.Name.Equals("alice", mode)).Should().StartWith("INDEX SEEK(Name = ");
            Ids(rows, row => row.Name.Equals("alice", mode)).Should().Equal(1);
            Ids(rows, row => row.Id > 0 && row.Name.Equals("alice", mode)).Should().Equal(1);
            Ids(rows, row => !row.Name.Equals("alice", mode) && row.Name.StartsWith("al", mode)).Should().BeEmpty();
            Ids(rows, row => row.Id == 2 || row.Name.Equals("alice", mode)).Should().Equal(1, 2);
        }

        [Fact]
        public void Modes_that_the_collation_does_not_provably_cover_keep_the_exact_scan()
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = new Collation("en-US/None") });
            var rows = db.GetCollection<Row>();
            rows.Insert(_names.Select((name, i) => new Row { Id = i + 1, Name = name }));
            rows.EnsureIndex(row => row.Name);

            Mode(rows, row => row.Name.Equals("alice", StringComparison.OrdinalIgnoreCase)).Should().StartWith("FULL INDEX SCAN");
            Ids(rows, row => row.Name.Equals("alice", StringComparison.OrdinalIgnoreCase)).Should().Equal(1, 2, 3);
        }

        [Fact]
        public void Cached_non_ordinal_mode_does_not_hide_a_later_ordinal_seek()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.Insert(_names.Select((name, i) => new Row { Id = i + 1, Name = name }));
            rows.EnsureIndex(row => row.Name);

            CapturedMode(rows, StringComparison.OrdinalIgnoreCase).Should().StartWith("FULL INDEX SCAN");
            CapturedMode(rows, StringComparison.Ordinal).Should().StartWith("INDEX SEEK(Name = ");
        }

        [Fact]
        public void A_row_dependent_mode_is_not_narrowed()
        {
            using var db = new LiteDatabase(":memory:");
            var rows = db.GetCollection<Row>();
            rows.Insert(new[] { new Row { Id = 4, Name = "alice" }, new Row { Id = 5, Name = "ALICE" } });

            Ids(rows, row => row.Name.Equals("alice", (StringComparison)row.Id)).Should().Equal(4, 5);
        }

        private static int[] Ids(ILiteCollection<Row> rows, Expression<Func<Row, bool>> predicate) =>
            rows.Find(predicate).Select(row => row.Id).OrderBy(id => id).ToArray();

        private static string Mode(ILiteCollection<Row> rows, Expression<Func<Row, bool>> predicate) =>
            rows.Query().Where(predicate).GetPlan()["index"]["mode"].AsString;

        private static string CapturedMode(ILiteCollection<Row> rows, StringComparison mode) =>
            Mode(rows, row => row.Name.Equals("alice", mode));
    }
}
