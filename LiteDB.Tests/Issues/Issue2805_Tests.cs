using System.Globalization;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2805_Tests
    {
        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string IName { get; set; }
            public string 名称 { get; set; }
        }

        [Theory]
        [InlineData("en-US", false)]
        [InlineData("tr-TR", false)]
        [InlineData("en-US", true)]
        [InlineData("tr-TR", true)]
        public void Generated_index_names_are_nonempty_distinct_and_idempotent(string culture, bool unicode)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                using var db = new LiteDatabase(":memory:");
                var col = db.GetCollection<Row>("rows");
                col.Insert(new[]
                {
                    new Row { Id = 1, Name = "a", IName = "b", 名称 = "c" },
                    new Row { Id = 2, Name = "b", IName = "a", 名称 = "d" }
                });
                if (unicode)
                {
                    col.EnsureIndex(x => x.名称).Should().BeTrue();
                    col.EnsureIndex(x => x.名称).Should().BeFalse();
                    col.Find(x => x.名称 == "c").Select(x => x.Id).Should().Equal(1);
                }
                else
                {
                    col.EnsureIndex(x => x.Name).Should().BeTrue();
                    col.EnsureIndex(x => x.IName).Should().BeTrue();
                    col.EnsureIndex(x => x.Name).Should().BeFalse();
                    col.EnsureIndex(x => x.IName).Should().BeFalse();
                    col.Find(x => x.Name == "a").Select(x => x.Id).Should().Equal(1);
                    col.Find(x => x.IName == "a").Select(x => x.Id).Should().Equal(2);
                }
                using var reader = db.Execute("SELECT name FROM $indexes WHERE collection = 'rows'");
                var names = reader.ToArray().Select(x => x["name"].AsString).ToArray();
                names.Should().HaveCount(unicode ? 2 : 3);
                names.Distinct().Should().HaveCount(names.Length);
                names.Should().OnlyContain(x => !string.IsNullOrEmpty(x));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }
    }
}
