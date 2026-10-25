using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2144_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("ja-JP")]
        public void Japanese_prefix_results_match_full_scan_before_and_after_indexing(string culture)
        {
            var rows = Enumerable.Range(0x3042, 0x3093 - 0x3042)
                .SelectMany(c => new[] { ((char)c) + "さ", "さ" + (char)c })
                .Select((name, i) => new Row { Id = i + 1, Name = name }).ToArray();
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = new Collation(culture + "/IgnoreCase") }))
            {
                var col = db.GetCollection<Row>();
                col.Insert(rows);
                var expected = rows.Where(x => CultureInfo.GetCultureInfo(culture).CompareInfo.IsPrefix(x.Name, "さ", CompareOptions.IgnoreCase))
                    .Select(x => x.Id).OrderBy(x => x).ToArray();
                expected.Should().NotBeEmpty();
                col.Find(x => x.Name.StartsWith("さ")).Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
                col.EnsureIndex(x => x.Name).Should().BeTrue();
                col.Find(x => x.Name.StartsWith("さ")).Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<Row>();
                col.FindAll().Select(x => x.Name).Should().BeEquivalentTo(rows.Select(x => x.Name));
                var expected = rows.Where(x => CultureInfo.GetCultureInfo(culture).CompareInfo.IsPrefix(x.Name, "さ", CompareOptions.IgnoreCase))
                    .Select(x => x.Id).OrderBy(x => x).ToArray();
                col.Find(x => x.Name.StartsWith("さ")).Select(x => x.Id).OrderBy(x => x).Should().Equal(expected);
            }
        }
    }
}
