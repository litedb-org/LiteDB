# Collections

Documents are stored and organized in collections. `LiteCollection` is the generic class that manages them in LiteDB. A collection name must:

* Contain only letters, numbers, or underscores (`_`).
* Be case-insensitive.
* Avoid the `_` prefix (reserved for internal storage).
* Avoid the `$` prefix (reserved for system and virtual collections).

The total size of all collection names in a database is limited to 8,000 bytes. If you expect to maintain hundreds of collections, keep the names short (about 10 characters per collection yields roughly 800 collections).

Collections are created automatically when you first call `Insert` or `EnsureIndex`. Read, update, or delete operations against a non-existent collection will fail rather than create it.

`LiteCollection<T>` works for both typed and untyped scenarios. When `T` is `BsonDocument`, LiteDB keeps the document schema-less; otherwise, it maps between `T` and `BsonDocument` under the hood. The two snippets below are equivalent:

```csharp
// Typed collection
using (var db = new LiteDatabase("mydb.db"))
{
    var customers = db.GetCollection<Customer>("customers");

    customers.Insert(new Customer { Id = 1, Name = "John Doe" });
    customers.EnsureIndex(x => x.Name);

    var match = customers.FindOne(x => x.Name == "john doe");
}

// Untyped collection (T is BsonDocument)
using (var db = new LiteDatabase("mydb.db"))
{
    var customers = db.GetCollection("customers");

    customers.Insert(new BsonDocument { ["_id"] = 1, ["Name"] = "John Doe" });
    customers.EnsureIndex("Name");

    var match = customers.FindOne("$.Name = 'john doe'");
}
```

## System Collections

System collections are special collections that provide information about the datafile. All system collections start with `$`. All system collections, with the exception of `$file`, are read-only.

| Collection | Description |
| --- | --- |
| $cols | Lists all collections in the datafile, including the system collections. |
| $database | Shows general info about the datafile. |
| $indexes | Lists all indexes in the datafile. |
| $sequences | Lists all the sequences in the datafile. |
| $transactions | Lists all the open transactions in the datafile. |
| $snapshots | Lists all existing snapshots. |
| $open\_cursors | Lists all the open cursors in the datafile. |
| $dump(pageID) | Lists advanced info about the desired page. If no pageID is provided, lists all the pages. |
| $page\_list(pageID) | Lists basic info about the desired page. If no pageID is provided, lists all the pages. |
| $query(subquery) | Takes a query as string and returns the result of the query. Can be used for simulating subqueries. **Experimental**. |
| $file(path) | See below. |

## The `$file` system collection

`$file` reads from and writes to external files.

* `SELECT $ INTO $FILE('customers.json') FROM Customers` exports every document from the `Customers` collection to a JSON file.
* `SELECT $ FROM $FILE('customers.json')` reads a JSON file back into the result set.

LiteDB also offers limited CSV support. Only primitive types are supported, and the schema of the first document controls the output columns. Additional fields are ignored when writing.

* `SELECT $ INTO $FILE('customers.csv') FROM Customers` exports the collection to CSV.
* `SELECT $ FROM $FILE('customers.csv')` imports rows from a CSV file.

The `$file` parameter can be one of the following:

* `$file("filename.json|csv")` — shorthand that infers the format from the file extension.
* `$file({ ... })` — detailed configuration using an object literal.

| Option | Applies to | Description |
| --- | --- | --- |
| `filename` | All | Target file path. |
| `format` | All | `"json"` or `"csv"`. |
| `encoding` | All | Text encoding (defaults to `"utf-8"`). |
| `overwrite` | All | Overwrite existing files when `true`. |
| `indent` | JSON | Indentation size used when `pretty` is `true`. |
| `pretty` | JSON | Emit indented JSON when `true`. |
| `delimiter` | CSV | Column separator (defaults to `","`). |
| `header` | CSV | When `true`, writes a header row; pass an array to control the header order when reading. |

---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
