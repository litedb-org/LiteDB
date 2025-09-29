# Getting Started

LiteDB is a simple, fast, and lightweight embedded .NET document database inspired by MongoDB. The API intentionally mirrors the official MongoDB .NET API so that common persistence patterns feel familiar.

## Installation

LiteDB is a serverless database—there is no service to install or configure. You can:

* Copy [LiteDB.dll](https://github.com/mbdavid/LiteDB/releases) next to your application binaries and reference it directly.
* Install the NuGet package with `Install-Package LiteDB`.

When hosting in IIS, be sure the application pool identity has write permissions to the folder that will contain your `.db` file.

## First steps

The snippet below creates a database, inserts a document, and runs a simple query:

```csharp
// Create your POCO class entity
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string[] Phones { get; set; }
    public bool IsActive { get; set; }
}

// Open database (or create if it doesn't exist)
using (var db = new LiteDatabase(@"C:\Temp\MyData.db"))
{
    // Get a collection (or create, if it doesn't exist)
    var customers = db.GetCollection<Customer>("customers");

    // Create your new customer instance
    var customer = new Customer
    {
        Name = "John Doe",
        Phones = new[] { "8000-0000", "9000-0000" },
        IsActive = true
    };

    // Insert new customer document (Id will be auto-incremented)
    customers.Insert(customer);

    // Update a document inside the collection
    customer.Name = "Jane Doe";
    customers.Update(customer);

    // Index documents using the Name property
    customers.EnsureIndex(x => x.Name);

    // Use LINQ to query documents (filter, sort, transform)
    var results = customers.Query()
        .Where(x => x.Name.StartsWith("J"))
        .OrderBy(x => x.Name)
        .Select(x => new { x.Name, NameUpper = x.Name.ToUpper() })
        .Limit(10)
        .ToList();

    // Create a multikey index on phone numbers
    customers.EnsureIndex(x => x.Phones);

    // Query by phone number
    var match = customers.FindOne(x => x.Phones.Contains("8888-5555"));
}
```

## Custom mapping

If the automatic mapper does not serialize a type the way you expect, register a custom serializer:

```csharp
BsonMapper.Global.RegisterType<DateTimeOffset>
(
    serialize: obj =>
    {
        var doc = new BsonDocument();
        doc["DateTime"] = obj.DateTime.Ticks;
        doc["Offset"] = obj.Offset.Ticks;
        return doc;
    },
    deserialize: doc => new DateTimeOffset(doc["DateTime"].AsInt64, new TimeSpan(doc["Offset"].AsInt64))
);
```

## Working with files

LiteDB ships with FileStorage for handling binary data:

```csharp
// Get file storage with an integer identifier
var storage = db.GetStorage<int>();

// Upload a file from the file system into the database
storage.Upload(123, @"C:\Temp\picture-01.jpg");

// And download it later
storage.Download(123, @"C:\Temp\copy-of-picture-01.jpg");
```

---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
