using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AwesomeAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Site_Tests
    {
        [Fact]
        public void Home_Example()
        {
            using (var f = new TempFile())
            using (var db = new LiteDatabase(f.Filename))
            {
                // Get customer collection
                var customers = db.GetCollection<Customer>("customers");

                // Create your new customer instance
                var customer = new Customer
                {
                    Name = "John Doe",
                    Phones = new string[] { "8000-0000", "9000-0000" },
                    IsActive = true
                };

                // Insert new customer document (Id will be auto-incremented)
                customers.Insert(customer);

                // Update a document inside a collection
                customer.Name = "Joana Doe";

                customers.Update(customer);

                // Index document using a document property
                customers.EnsureIndex(x => x.Name);

                // Now, let's create a simple query
                var results = customers.Find(x => x.Name.StartsWith("Jo")).ToList();

                results.Count.Should().Be(1);

                // Or you can query using new Query() syntax
                var results2 = customers.Query()
                    .Where(x => x.Phones.Any(p => p.StartsWith("8000")))
                    .OrderBy(x => x.Name)
                    .Select(x => new { x.Id, x.Name })
                    .Limit(10)
                    .ToList();

                // Or using SQL
                var reader = db.Execute(
                    @"SELECT _id, Name 
                        FROM customers 
                       WHERE Phones ANY LIKE '8000%'
                       ORDER BY Name
                       LIMIT 10");

                results2.Count.Should().Be(1);
                reader.ToList().Count.Should().Be(1);

            }
        }

        public class Customer
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string[] Phones { get; set; }
            public bool IsActive { get; set; }
        }
    }
}