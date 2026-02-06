using System;
using System.Collections.Generic;
using Xunit;
using System.Linq;
using LiteDB.Tests.Utils;

namespace LiteDB.Tests.Issues
{
    
    public class Issue1651_Tests
    {
        public class Order : BaseEntity
        {
            public Customer Customer { get; set; }
        }

        public class Customer : BaseEntity
        {
            public string Name { get; set; }
        }

        public class BaseEntity
        {
            public Guid Id { get; set; }
        }

        [Fact]
        public void Find_ByRelationId_Success()
        {
            BsonMapper.Global.Entity<Order>().DbRef(order => order.Customer);

            using var _database = DatabaseFactory.Create();
            var _orderCollection = _database.GetCollection<Order>("Order");
            var _customerCollection = _database.GetCollection<Customer>("Customer");

            var customer = new Customer() { Name = "John", };

            Assert.True(_customerCollection.Upsert(customer));
            Assert.True(_customerCollection.Upsert(new Customer() { Name = "Anonymous" }));

            Assert.NotEqual(Guid.Empty, customer.Id);

            var order = new Order()
            {
                Customer = customer,
            };
            var order2 = new Order()
            {
                Customer = new Customer() { Id = customer.Id },
            };
            var orphanOrder = new Order();

            Assert.True(_orderCollection.Upsert(orphanOrder));
            Assert.True(_orderCollection.Upsert(order));
            Assert.True(_orderCollection.Upsert(order2));

            customer.Name = "Josh";
            Assert.True(_customerCollection.Update(customer));

            var actualOrders = _orderCollection
                .Include(orderEntity => orderEntity.Customer)
                .Find(orderEntity => orderEntity.Customer.Id == customer.Id)
                .ToList();

            Assert.Equal(2, actualOrders.Count);
            Assert.Equal(new[] { customer.Name, customer.Name },
                actualOrders.Select(actualOrder => actualOrder.Customer.Name));
            Assert.Equal(2, (_customerCollection.FindAll().ToList()).Count);
            Assert.Equal(3, (_orderCollection.FindAll().ToList()).Count);
        }
    }
}
