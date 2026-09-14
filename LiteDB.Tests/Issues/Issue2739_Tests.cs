using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2739_Tests
    {
        public class Target
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")]
            public Target Reference { get; set; }
        }

        [Fact]
        public void UpdateMany_stores_a_reference_and_later_includes_current_target_data()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var oldTarget = new Target { Id = 1, Name = "old" };
                var newTarget = new Target { Id = 2, Name = "new" };
                var targets = db.GetCollection<Target>("targets");
                targets.Insert(new[] { oldTarget, newTarget });
                var rows = db.GetCollection<Row>("rows");
                rows.Insert(new[] { new Row { Id = 11, Reference = oldTarget }, new Row { Id = 12, Reference = oldTarget } });
                rows.UpdateMany(x => new Row { Id = x.Id, Reference = newTarget }, x => x.Id == 11).Should().Be(1);
                newTarget.Name = "changed after assignment";
                targets.Update(newTarget).Should().BeTrue();
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var raw = db.GetCollection("rows");
                var reference = raw.FindById(11)["Reference"].AsDocument;
                reference.Keys.Should().BeEquivalentTo("$id", "$ref");
                reference["$id"].AsInt32.Should().Be(2);
                reference["$ref"].AsString.Should().Be("targets");
                var rows = db.GetCollection<Row>("rows").Include(x => x.Reference);
                rows.FindById(11).Reference.Name.Should().Be("changed after assignment");
                rows.FindById(12).Reference.Name.Should().Be("old");
                raw.Count().Should().Be(2);
            }
        }
    }
}
