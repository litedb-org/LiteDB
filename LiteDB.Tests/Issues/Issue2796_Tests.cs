using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2796_Tests
    {
        public class Child { public int Id { get; set; } public byte[] Data { get; set; } }
        public class Parent
        {
            public int Id { get; set; }
            public byte[] Data { get; set; }
            [BsonRef("children")] public Child Child { get; set; }
        }

        [Fact]
        public void Root_limit_includes_embedded_documents_but_not_reference_targets()
        {
            using var file = new TempFile();
            var payload = Enumerable.Repeat((byte)0x6a, 10 * 1024 * 1024).ToArray();
            using (var db = new LiteDatabase(file.Filename))
            {
                var child = new Child { Id = 10, Data = payload };
                db.GetCollection<Child>("children").Insert(child);
                db.GetCollection<Parent>("parents").Insert(new Parent { Id = 20, Data = payload, Child = child });
                var raw = db.GetCollection("parents").FindById(20);
                raw["Child"].AsDocument.Keys.Should().BeEquivalentTo("$id", "$ref");
                Action tooLarge = () => db.GetCollection("parents").Insert(new BsonDocument
                {
                    ["_id"] = 30, ["Data"] = payload, ["Child"] = new BsonDocument { ["Data"] = payload }
                });
                tooLarge.Should().Throw<LiteException>();
                db.GetCollection("parents").Count().Should().Be(1);
            }
            using var reopened = new LiteDatabase(file.Filename);
            var parent = reopened.GetCollection<Parent>("parents").Include(x => x.Child).FindById(20);
            parent.Data.Should().Equal(payload);
            parent.Child.Id.Should().Be(10);
            parent.Child.Data.Should().Equal(payload);
            Assert.Null(reopened.GetCollection("parents").FindById(30));
        }
    }
}
