using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1899_Tests
    {
        public class Target { public int Id { get; set; } public string Name { get; set; } }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")] public Target Document { get; set; }
        }

        [Fact]
        public void Paged_QueryAll_preserves_include_and_uses_current_referenced_data()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var targets = db.GetCollection<Target>("targets");
                var col = db.GetCollection<Row>("rows");
                for (var i = 1; i <= 4; i++)
                {
                    var target = new Target { Id = i + 10, Name = "old" };
                    targets.Insert(target);
                    col.Insert(new Row { Id = i, Document = target });
                    target.Name = "fresh " + i;
                    targets.Update(target).Should().BeTrue();
                }
            }
            using var reopened = new LiteDatabase(file.Filename);
            var rows = reopened.GetCollection<Row>("rows").Include(x => x.Document).Find(Query.All(), 1, 2).ToArray();
            rows.Select(x => x.Id).Should().Equal(2, 3);
            rows.Select(x => x.Document.Id).Should().Equal(12, 13);
            rows.Select(x => x.Document.Name).Should().Equal("fresh 2", "fresh 3");
            reopened.GetCollection("rows").FindById(2)["Document"].AsDocument.Keys.Should().BeEquivalentTo("$id", "$ref");
        }
    }
}
