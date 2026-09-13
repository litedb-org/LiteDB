using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class DbRef_Index_Tests
    {
        #region Model

        public class Customer
        {
            public string Login { get; set; }
            public string Name { get; set; }
        }

        public class Order
        {
            public int OrderNumber { get; set; }
            public Customer Customer { get; set; }
        }

        #endregion

        [Fact]
        public void DbRef_Index()
        {
            var mapper = new BsonMapper();

            mapper.Entity<Customer>()
                .Id(x => x.Login)
                .Field(x => x.Name, "customer_name");

            mapper.Entity<Order>()
                .Id(x => x.OrderNumber)
                .Field(x => x.Customer, "cust")
                .DbRef(x => x.Customer, "customers");

            using (var db = new LiteDatabase(new MemoryStream(), mapper, new MemoryStream()))
            {
                var customer = new Customer { Login = "jd", Name = "John Doe" };
                var order = new Order { Customer = customer };

                var customers = db.GetCollection<Customer>("Customers");
                var orders = db.GetCollection<Order>("Orders");

                customers.Insert(customer);
                orders.Insert(order);

                // create an index in Customer.Id ref
                // x.Customer.Login == "Customer.$id"
                orders.EnsureIndex(x => x.Customer.Login);

                var query = orders
                    .Include(x => x.Customer)
                    .FindOne(x => x.Customer.Login == "jd");

                query.Customer.Name.Should().Be(customer.Name);
            }
        }
    }
}