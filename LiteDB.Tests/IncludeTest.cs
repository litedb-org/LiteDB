﻿using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using LiteDB;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;

namespace UnitTest
{
    public class Order
    {
        public ObjectId Id { get; set; }
        public Customer Customer { get; set; }
        public Customer CustomerNull { get; set; }

        public List<Product> Products { get; set; }
        public Product[] ProductArray { get; set; }
        public ICollection<Product> ProductColl { get; set; }
        public List<Product> ProductEmpty { get; set; }
        public List<Product> ProductsNull { get; set; }
    }

    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class Product
    {
        public int ProductId { get; set; }
        public string Name { get; set; }
        public decimal Price { get; set; }
    }

    [TestClass]
    public class IncludeTest
    {
        [TestMethod]
        public void Include_Test()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var customers = db.GetCollection<Customer>("customers");
                var products = db.GetCollection<Product>("products");
                var orders = db.GetCollection<Order>("orders");

                db.Mapper.Entity<Order>()
                    .DbRef(x => x.Products, "products")
                    .DbRef(x => x.ProductArray, "products")
                    .DbRef(x => x.ProductColl, "products")
                    .DbRef(x => x.ProductEmpty, "products")
                    .DbRef(x => x.ProductsNull, "products")
                    .DbRef(x => x.Customer, "customers")
                    .DbRef(x => x.CustomerNull, "customers");

                var customer = new Customer { Name = "John Doe" };

                var product1 = new Product { Name = "TV", Price = 800 };
                var product2 = new Product { Name = "DVD", Price = 200 };

                // insert ref documents
                customers.Insert(customer);
                products.Insert(new Product[] { product1, product2 });

                var order = new Order
                {
                    Customer = customer,
                    CustomerNull = null,
                    Products = new List<Product>() { product1, product2 },
                    ProductArray = new Product[] { product1 },
                    ProductColl = new List<Product>() { product2 },
                    ProductEmpty = new List<Product>(),
                    ProductsNull = null
                };

                var orderJson = JsonSerializer.Serialize(db.Mapper.ToDocument(order), true);

                var nOrder = db.Mapper.Deserialize<Order>(JsonSerializer.Deserialize(orderJson));

                orders.Insert(order);

                var query = orders
                    .Include(x => x.Customer)
                    .Include(x => x.CustomerNull)
                    .Include(x => x.Products)
                    .Include(x => x.ProductArray)
                    .Include(x => x.ProductColl)
                    .Include(x => x.ProductsNull)
                    .FindAll()
                    .FirstOrDefault();

                var customerName = query.Customer.Name;

            }
        }
    }
}
