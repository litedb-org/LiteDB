using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1904_Tests
    {
        public class Home { public int Id { get; set; } public int Value { get; set; } }
        public class Human
        {
            public int Id { get; set; }
            [BsonRef("homes")] public List<Home> Homes { get; set; }
        }

        [Fact]
        public void Adding_reference_field_index_cannot_remove_included_matches()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                var a = new Home { Id = 10, Value = 65 };
                var b = new Home { Id = 20, Value = 85 };
                db.GetCollection<Home>("homes").Insert(new[] { a, b });
                var col = db.GetCollection<Human>("humans");
                col.Insert(new[]
                {
                    new Human { Id = 1, Homes = new List<Home> { a } },
                    new Human { Id = 2, Homes = new List<Home> { b } },
                    new Human { Id = 3, Homes = new List<Home> { a, b } }
                });
                Check(col);
                col.EnsureIndex("home_value", "$.Homes[*].Value");
                Check(col);
            }
            using var reopened = new LiteDatabase(file.Filename);
            Check(reopened.GetCollection<Human>("humans"));
            reopened.GetCollection<Home>("homes").Count().Should().Be(2);
        }

        private static void Check(ILiteCollection<Human> col)
        {
            col.Include(x => x.Homes).Find(Query.Any().EQ("$.Homes[*].Value", 65))
                .Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 3);
            col.Include(x => x.Homes).Find(Query.Any().EQ("$.Homes[*].Value", 85))
                .Select(x => x.Id).OrderBy(x => x).Should().Equal(2, 3);
        }
    }
}
