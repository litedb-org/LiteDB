using System.Linq;
using FluentAssertions;
using LiteDB.Tests.Expressions;
using Xunit;

namespace LiteDB.Tests.QueryTest
{
    public class SqlLikeExecution_Tests
    {
        [Theory]
        [InlineData("en-US/IgnoreCase", "a")]
        [InlineData("en-US/IgnoreCase, IgnoreNonSpace", "e")]
        [InlineData("en-US/Ordinal", "A")]
        [InlineData("en-US/OrdinalIgnoreCase", "a")]
        [InlineData("tr-TR/IgnoreCase", "i")]
        [InlineData("ja-JP/IgnoreKanaType, IgnoreWidth", "カ")]
        public void Linq_sql_and_full_index_like_keep_results_with_changing_parameters(string culture, string needle)
        {
            var collation = new Collation(culture);
            using var db = new LiteDatabase(new ConnectionString { Filename = ":memory:", Collation = collation });
            var rows = db.GetCollection<Row>("rows");
            var data = new[] { "Alpha", "banana", "Café", "Élan", "cafe\u0301", "Iİiı", "カｶか", "😀a", "plain", "" }
                .Select((name, i) => new Row { Id = i + 1, Name = name }).ToArray();
            rows.InsertBulk(data);
            foreach (var current in new[] { needle, "plain", needle })
            {
                var pattern = "%" + current + "%";
                var expected = data.Where(x => SqlLikeReference.Match(x.Name, pattern, collation)).Select(x => x.Id).ToArray();
                rows.Find(x => x.Name.Contains(current)).Select(x => x.Id).Should().BeEquivalentTo(expected);
                using var reader = db.Execute("SELECT _id FROM rows WHERE Name LIKE @pattern", new BsonDocument { ["pattern"] = pattern });
                reader.ToArray().Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(expected);
            }
            var contains = "%" + needle + "%";
            var matches = data.Where(x => SqlLikeReference.Match(x.Name, contains, collation)).Select(x => x.Id).ToArray();
            rows.EnsureIndex(x => x.Name);
            var indexed = rows.Query().Where(x => x.Name.Contains(needle));
            indexed.GetPlan()["index"]["name"].AsString.Should().Be("Name");
            indexed.ToArray().Select(x => x.Id).Should().BeEquivalentTo(matches);
            rows.Count(x => x.Name.Contains(needle)).Should().Be(matches.Length);
        }

        public class Row
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
    }
}
