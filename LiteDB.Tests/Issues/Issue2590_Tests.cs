using System;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2590_Tests
    {
        public class Row
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public Row(string id, string name) { Id = id; Name = name; }
            public Row(string name) : this(string.Empty, name) { }
        }

        [Fact]
        public void Empty_string_id_does_not_throw_after_committing_an_unaddressable_document()
        {
            using var file = new TempFile();
            var input = new Row("target");
            BsonValue id = null;
            Exception failure;
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<Row>("rows");
                col.Insert(new Row("control", "untouched"));
                failure = Record.Exception(() => id = col.Insert(input));
            }
            using (var db = new LiteDatabase(file.Filename))
            using (new AssertionScope())
            {
                var raw = db.GetCollection("rows");
                raw.FindById("control")["Name"].AsString.Should().Be("untouched");
                if (failure != null)
                {
                    (failure is LiteException || failure is ArgumentException).Should().BeTrue(
                        "an unsupported string auto-id must be validated before the engine writes");
                    raw.Count().Should().Be(1, "failure must leave no generated ObjectId document behind");
                }
                else
                {
                    id.Should().NotBeNull();
                    id.IsString.Should().BeTrue();
                    id.AsString.Should().Be(input.Id);
                    db.GetCollection<Row>("rows").FindById(input.Id).Name.Should().Be("target");
                    raw.Count().Should().Be(2);
                }
            }
        }
    }
}
