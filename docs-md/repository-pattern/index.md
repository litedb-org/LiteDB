Repository Pattern - Hugo Whisper Theme



[Fork me on GitHub](https://github.com/mbdavid/litedb)

* [HOME](www.example.com/)
* [DOCS](www.example.com/docs/)
* [API](www.example.com/api/)
* [DOWNLOAD](https://www.nuget.org/packages/LiteDB/)

[![Logo](/www.example.com/logo_litedb.svg)](www.example.com)

[![Logo](/www.example.com/logo_litedb.svg)](www.example.com)

* [HOME](www.example.com/)
* [DOCS](www.example.com/docs/)
* [API](www.example.com/api/)
* [DOWNLOAD](https://www.nuget.org/packages/LiteDB/)

#### Docs

* [BsonDocument](www.example.com/docs/bsondocument/)
* [ChangeLog](www.example.com/docs/changelog/)
* [Collections](www.example.com/docs/collections/)
* [Concurrency](www.example.com/docs/concurrency/)
* [Connection String](www.example.com/docs/connection-string/)
* [Data Structure](www.example.com/docs/data-structure/)
* [DbRef](www.example.com/docs/dbref/)
* [Expressions](www.example.com/docs/expressions/)
* [FileStorage](www.example.com/docs/filestorage/)
* [Getting Started](www.example.com/docs/getting-started/)
* [How LiteDB Works](www.example.com/docs/how-litedb-works/)
* [Indexes](www.example.com/docs/indexes/)
* [Object Mapping](www.example.com/docs/object-mapping/)
* [Queries](www.example.com/docs/queries/)
* [Repository Pattern](www.example.com/docs/repository-pattern/)

# Repository Pattern

`LiteRepository` is a new class to access your database. LiteRepository is implemented over `LiteDatabase` and is just a layer to quick access your data without `LiteCollection` class and fluent query

```
using(var db = new LiteRepository(connectionString))
{
    // simple access to Insert/Update/Upsert/Delete
    db.Insert(new Product { ProductName = "Table", Price = 100 });

    db.Delete<Product>(x => x.Price == 100);

    // query using fluent query
    var result = db.Query<Order>()
        .Include(x => x.Customer) // add dbref 1x1
        .Include(x => x.Products) // add dbref 1xN
        .Where(x => x.Date == DateTime.Today) // use indexed query
        .Where(x => x.Active) // used indexes query
        .ToList();

    var p = db.Query<Product>()
        .Where(x => x.ProductName.StartsWith("Table"))
        .Where(x => x.Price < 200)
        .Limit(10)
        .ToEnumerable();

    var c = db.Query<Customer>()
        .Where(txtName.Text != null, x => x.Name == txtName.Text) // conditional filter
        .ToList();

}
```

Collection names could be omited and will be resolved by new `BsonMapper.ResolveCollectionName` function (default: `typeof(T).Name`).

This API was inspired by this great project [NPoco Micro-ORM](https://github.com/schotime/NPoco)

* [www.zerostatic.io](https://www.zerostatic.io)
