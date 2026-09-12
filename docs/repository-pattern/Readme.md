# Repository Pattern

`LiteRepository` is a new class to access your database. LiteRepository is implemented over `LiteDatabase` and is just a layer to quick access your data without `LiteCollection` class and fluent query

```csharp
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

This API was inspired by the excellent [NPoco Micro-ORM](https://github.com/schotime/NPoco).

---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
