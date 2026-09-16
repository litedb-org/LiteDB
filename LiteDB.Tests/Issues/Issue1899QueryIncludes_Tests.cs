using System.Linq;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1899QueryIncludes_Tests
    {
        public class Address { public int Id { get; set; } public string Street { get; set; } }
        public class Target
        {
            public int Id { get; set; }
            public string Name { get; set; }
            [BsonRef("addresses")] public Address Address { get; set; }
        }
        public class Row
        {
            public int Id { get; set; }
            [BsonRef("targets")] public Target First { get; set; }
            [BsonRef("targets")] public Target Second { get; set; }
        }

        [Fact]
        public void Collection_parent_include_precedes_nested_query_include_like_Query_composition()
        {
            using var db = new LiteDatabase(":memory:");
            var address = new Address { Id = 1, Street = "Main" };
            db.GetCollection<Address>("addresses").Insert(address);
            var target = new Target { Id = 1, Name = "Customer", Address = address };
            db.GetCollection<Target>("targets").Insert(target);
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new Row { Id = 1, First = target });
            var query = Query.All();
            query.Includes.Add("$.First.Address");
            var collection = rows.Include(row => row.First);
            var result = collection.Find(query).Single();
            var fluent = collection.Query().Include("$.First.Address").First();
            Assert.Equal("Main", result.First.Address.Street);
            Assert.Equal(fluent.First.Address.Street, result.First.Address.Street);
            Assert.Single(query.Includes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Find_merges_collection_and_query_includes_without_mutating_the_query(bool paging)
        {
            using var db = new LiteDatabase(":memory:");
            var targets = db.GetCollection<Target>("targets");
            var first = new Target { Id = 1, Name = "first" };
            var second = new Target { Id = 2, Name = "second" };
            targets.Insert(new[] { first, second });
            var rows = db.GetCollection<Row>("rows");
            rows.Insert(new[]
            {
                new Row { Id = 1, First = first, Second = second },
                new Row { Id = 2, First = first, Second = second }
            });
            first.Name = "fresh first";
            targets.Update(first);
            var query = Query.All(Query.Descending);
            query.Includes.Add("$.Second");
            var included = rows.Include(row => row.First);
            var result = included.Find(query, paging ? 1 : 0, paging ? 1 : int.MaxValue).ToArray();
            Assert.Equal(paging ? new[] { 1 } : new[] { 2, 1 }, result.Select(row => row.Id));
            Assert.All(result, row =>
            {
                Assert.Equal("fresh first", row.First.Name);
                Assert.Equal("second", row.Second.Name);
            });
            Assert.Single(query.Includes);
            Assert.Equal("$.Second", query.Includes.Single().Source);
            Assert.Equal(0, query.Offset);
            Assert.Equal(int.MaxValue, query.Limit);
            Assert.All(rows.Find(query), row => Assert.Null(row.First.Name));
            Assert.Equal("fresh first", included.FindOne(query).First.Name);
        }
    }
}
