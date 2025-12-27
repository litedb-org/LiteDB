# LiteDB - A .NET NoSQL Document Store in a single data file

[![NuGet Version](https://img.shields.io/nuget/v/LiteDB)](https://www.nuget.org/packages/LiteDB/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LiteDB)](https://www.nuget.org/packages/LiteDB/)
[![Build status](https://ci.appveyor.com/api/projects/status/sfe8he0vik18m033?svg=true)](https://ci.appveyor.com/project/mbdavid/litedb) 
[![](https://dcbadge.limes.pink/api/server/u8seFBH9Zu?style=flat-square)](https://discord.gg/u8seFBH9Zu)


LiteDB is a small, fast and lightweight .NET NoSQL embedded database. 

- Serverless NoSQL Document Store
- Simple API, similar to MongoDB
- 100% C# code for .NET 4.5 / NETStandard 1.3/2.0 in a single DLL (less than 450kb)
- Thread-safe
- ACID with full transaction support
- Data recovery after write failure (WAL log file)
- Datafile encryption using DES (AES) cryptography
- Map your POCO classes to `BsonDocument` using attributes or fluent mapper API
- Store files and stream data (like GridFS in MongoDB)
- Single data file storage (like SQLite)
- Index document fields for fast search
- LINQ support for queries
- SQL-Like commands to access/transform data
- [LiteDB Studio](https://github.com/mbdavid/LiteDB.Studio) - Nice UI for data access 
- Open source and free for everyone - including commercial use
- Install from NuGet: `Install-Package LiteDB`


## New v5

- New storage engine
- No locks for `read` operations (multiple readers)
- `Write` locks per collection (multiple writers)
- Internal/System collections 
- New `SQL-Like Syntax`
- New query engine (support projection, sort, filter, query)
- Partial document load (root level)
- and much, much more!

## Lite.Studio

New UI to manage and visualize your database:


![LiteDB.Studio](https://www.litedb.org/images/banner.gif)

## Documentation

Visit [the Wiki](https://github.com/mbdavid/LiteDB/wiki) for full documentation. For simplified chinese version, [check here](https://github.com/lidanger/LiteDB.wiki_Translation_zh-cn).

## LiteDB Community

Help LiteDB grow its user community by answering this [simple survey](https://docs.google.com/forms/d/e/1FAIpQLSc4cNG7wyLKXXcOLIt7Ea4TlXCG6s-51_EfHPu2p5WZ2dIx7A/viewform?usp=sf_link)

## How to use LiteDB

A quick example for storing and searching documents:

```C#
// Create your POCO class
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
    public int Age { get; set; }
    public string[] Phones { get; set; }
    public bool IsActive { get; set; }
}

// Open database (or create if doesn't exist)
using(var db = new LiteDatabase(@"MyData.db"))
{
    // Get customer collection
    var col = db.GetCollection<Customer>("customers");

    // Create your new customer instance
    var customer = new Customer
    { 
        Name = "John Doe", 
        Phones = new string[] { "8000-0000", "9000-0000" }, 
        Age = 39,
        IsActive = true
    };

    // Create unique index in Name field
    col.EnsureIndex(x => x.Name, true);

    // Insert new customer document (Id will be auto-incremented)
    col.Insert(customer);

    // Update a document inside a collection
    customer.Name = "Joana Doe";

    col.Update(customer);

    // Use LINQ to query documents (with no index)
    var results = col.Find(x => x.Age > 20);
}
```

Using fluent mapper and cross document reference for more complex data models

```C#
// DbRef to cross references
public class Order
{
    public ObjectId Id { get; set; }
    public DateTime OrderDate { get; set; }
    public Address ShippingAddress { get; set; }
    public Customer Customer { get; set; }
    public List<Product> Products { get; set; }
}        

// Re-use mapper from global instance
var mapper = BsonMapper.Global;

// "Products" and "Customer" are from other collections (not embedded document)
mapper.Entity<Order>()
    .DbRef(x => x.Customer, "customers")   // 1 to 1/0 reference
    .DbRef(x => x.Products, "products")    // 1 to Many reference
    .Field(x => x.ShippingAddress, "addr"); // Embedded sub document
            
using(var db = new LiteDatabase("MyOrderDatafile.db"))
{
    var orders = db.GetCollection<Order>("orders");
        
    // When query Order, includes references
    var query = orders
        .Include(x => x.Customer)
        .Include(x => x.Products) // 1 to many reference
        .Find(x => x.OrderDate <= DateTime.Now);

    // Each instance of Order will load Customer/Products references
    foreach(var order in query)
    {
        var name = order.Customer.Name;
        ...
    }
}

```

## Where to use?

- Desktop/local small applications
- Application file format
- Small web sites/applications
- One database **per account/user** data store

## Plugins

- A GUI viewer tool: https://github.com/falahati/LiteDBViewer (v4)
- A GUI editor tool: https://github.com/JosefNemec/LiteDbExplorer (v4)
- Lucene.NET directory: https://github.com/sheryever/LiteDBDirectory
- LINQPad support: https://github.com/adospace/litedbpad
- F# Support: https://github.com/Zaid-Ajaj/LiteDB.FSharp (v4)
- UltraLiteDB (for Unity or IOT): https://github.com/rejemy/UltraLiteDB
- OneBella - cross platform (windows, macos, linux) GUI tool : https://github.com/namigop/OneBella
- LiteDB.Migration: Framework that makes schema migrations easier: https://github.com/JKamsker/LiteDB.Migration/

## Changelog

Change details for each release are documented in the [release notes](https://github.com/mbdavid/LiteDB/releases).

## Code Signing

LiteDB is digitally signed courtesy of [SignPath](https://www.signpath.io)

<a href="https://www.signpath.io">
    <img src="https://about.signpath.io/assets/signpath-logo.svg" width="150">
</a>

## License

[MIT](http://opensource.org/licenses/MIT)
