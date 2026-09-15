using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1920_Tests
    {
        public class Target { public int Id { get; set; } public string Name { get; set; } }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")] public Target Course { get; set; }
        }

        [Fact]
        public void Projecting_included_reference_preserves_its_id_and_payload()
        {
            using var db = new LiteDatabase(":memory:");
            var target = new Target { Id = 37, Name = "course" };
            db.GetCollection<Target>("targets").Insert(target);
            var col = db.GetCollection<Row>("rows");
            col.Insert(new Row { Id = 1, Course = target });
            col.Include(x => x.Course).FindById(1).Course.Id.Should().Be(37);
            var projected = col.Include(x => x.Course).Query().Where(x => x.Id == 1)
                .Select(x => new { Article = x.Course, ParentId = x.Id }).Single();
            projected.ParentId.Should().Be(1);
            projected.Article.Id.Should().Be(37);
            projected.Article.Name.Should().Be("course");
            db.GetCollection<Target>("targets").FindById(projected.Article.Id).Name.Should().Be("course");
            db.GetCollection("rows").FindById(1)["Course"]["$id"].AsInt32.Should().Be(37);
        }
    }
}
