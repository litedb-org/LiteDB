using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class MultiKey_Mapper_Tests
    {
        #region Model

        public class MultiKeyDoc
        {
            public int Id { get; set; }
            public int[] Keys { get; set; }
            public List<Customer> Customers { get; set; }
        }

        public class Customer
        {
            public string Login { get; set; }
            public string Name { get; set; }
        }

        #endregion

        [Fact]
        public void MultiKey_Mapper()
        {
            using (var db = new LiteDatabase(":memory:"))
            {
                var col = db.GetCollection<MultiKeyDoc>("col");

                col.Insert(new MultiKeyDoc
                {
                    Id = 1,
                    Keys = new int[] { 1, 2, 3 },
                    Customers = new List<Customer>()
                    {
                        new Customer { Name = "John" },
                        new Customer { Name = "Ana" },
                        new Customer { Name = "Doe" },
                        new Customer { Name = "Dante" }
                    }
                });

                col.Insert(new MultiKeyDoc
                {
                    Id = 2,
                    Keys = new int[] { 2 },
                    Customers = new List<Customer>()
                    {
                        new Customer { Name = "Ana" }
                    }
                });

                col.EnsureIndex(x => x.Keys);
                col.EnsureIndex(x => x.Customers.Select(z => z.Name));

                // Query.EQ("Keys", 2)
                col.Count(Query.Any().EQ("Keys", 2)).Should().Be(2);
                col.Count(x => x.Keys.Contains(2)).Should().Be(2);

                col.Count(Query.Any().StartsWith("Customers[*].Name", "Ana")).Should().Be(2);
                col.Count(x => x.Customers.Select(z => z.Name).Any(z => z.StartsWith("Ana"))).Should().Be(2);

                col.Count(Query.Any().StartsWith("Customers[*].Name", "D")).Should().Be(1);
                col.Count(x => x.Customers.Select(z => z.Name).Any(z => z.StartsWith("D"))).Should().Be(1);
            }
        }
    }
}