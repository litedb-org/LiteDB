using System.Linq;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2811_Tests
    {
        public class Row
        {
            public int Id { get; }
            public string Name { get; }
            public Row(int id, string name) { Id = id; Name = name; }
        }

        [Fact]
        public void Read_only_auto_id_either_commits_successfully_or_rejects_without_a_write()
        {
            using var file = new TempFile();
            BsonValue inserted = null;
            System.Exception failure;
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var col = db.GetCollection<Row>("rows");
                col.Insert(new Row(91, "control"));
                failure = Record.Exception(() => inserted = col.Insert(new Row(0, "target")));
            }
            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            using (new AssertionScope())
            {
                var raw = db.GetCollection("rows");
                raw.FindById(91)["Name"].AsString.Should().Be("control");
                if (failure != null)
                {
                    failure.Should().BeOfType<LiteException>("invalid auto-id configuration needs a deliberate validation error");
                    raw.FindAll().Select(x => x["_id"].AsInt32).Should().Equal(new[] { 91 },
                        "a failed insertion must not silently persist the target");
                }
                else
                {
                    inserted.Should().NotBeNull();
                    inserted.AsInt32.Should().NotBe(0);
                    raw.FindById(inserted)["Name"].AsString.Should().Be("target");
                    raw.Count().Should().Be(2);
                }
            }
        }
    }
}
