using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1002_Tests
    {
        public class Row { public int? Id { get; set; } public string Value { get; set; } }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        public void Nullable_integer_empty_ids_generate_unique_persisted_integer_keys(int? empty)
        {
            using var file = new TempFile();
            var first = new Row { Id = empty, Value = "first" };
            var second = new Row { Id = empty, Value = "second" };
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<Row>("rows");
                var firstId = col.Insert(first);
                var secondId = col.Insert(second);
                firstId.IsInt32.Should().BeTrue();
                secondId.IsInt32.Should().BeTrue();
                first.Id.Should().Be(firstId.AsInt32);
                second.Id.Should().Be(secondId.AsInt32);
                first.Id.Should().NotBe(second.Id);
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection<Row>("rows").FindById(first.Id.Value).Value.Should().Be("first");
            reopened.GetCollection<Row>("rows").FindById(second.Id.Value).Value.Should().Be("second");
            reopened.GetCollection("rows").FindAll().Should().OnlyContain(x => x["_id"].IsInt32);
        }
    }
}
