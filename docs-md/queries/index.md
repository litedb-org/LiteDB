Queries - Hugo Whisper Theme



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

# Queries

Query filter document inside a collection in three ways:

* Indexed based search (best option). See <Indexes>
* Full scan on BsonDocument (slower but more powerful)
* LINQ to object (slower but convenient)

### Query implementations

`Query` is a static class that creates a query criteria. Each method represents a different criteria operation that can be used to query documents.

* **`Query.All`** - Returns all documents. Can be specified an index field to read in ascending or descending index order.
* **`Query.EQ`** - Find document are equals (==) to value.
* **`Query.LT/LTE`** - Find documents less then (<) or less then equals (<=) to value.
* **`Query.GT/GTE`** - Find documents greater then (>) or greater then equals (>=) to value.
* **`Query.Between`** - Find documents between start/end value.
* **`Query.In`** - Find documents that are equals of listed values.
* **`Query.Not`** - Find documents that are NOT equals (!=) to value.
* **`Query.StartsWith`** - Find documents that strings starts with value. Valid only for string data type.
* **`Query.Contains`** - Find documents that strings contains value. Valid only for string data type. This query do index search, only index scan (slow for many documents).
* **`Query.Where`** - Find documents based in a `Func<BsonValue, bool>` predicate, where `BsonValue` are each key in index. It’s a full index scan based query.
* **`Query.And`** - Apply intersection between two queries results.
* **`Query.Or`** - Apply union between two queries results.

```
var results = col.Find(Query.EQ("Name", "John Doe"));

var results = col.Find(Query.GTE("Age", 25));

var results = col.Find(Query.And(
    Query.EQ("FirstName", "John"), Query.EQ("LastName", "Doe")
));

var results = col.Find(Query.StartsWith("Name", "Jo"));

// Query using multikey index (where products are an array of embedded documents)
var results = col.Find(Query.GT("Products[*].Price", 100))

// Execute Func in each key in Name index
var results = col.Find(Query.Where("Name", name => name.AsString.Length > 20));

// get last added 100 objects of the collection
var results = collection.Find(Query.All(Query.Descending), limit: 100);

// find top 100 oldest persons aged between 20 and 30
var results = col.Find(Query.And(Query.All("Age", Query.Descending), Query.Between("Age", 20, 30)), limit: 100);
```

In all queries:

* In index search, **Field** must be an index name or field in document.
* When no index using, **Field** can be `Path` or an `Expression`
* **Field** name on left side, **Value** (or values) on right side
* Queries are executed in `BsonDocument` class before mapping to your object. You need to use the `BsonDocument` field name and BSON types values. If you are using a custom `ResolvePropertyName` or `[BsonField]` attribute, you must use your document field name and not the property name on your type. See [Object Mapping](Object-Mapping).

### Find(), FindById(), FindOne() and FindAll()

Collections are 4 ways to return documents:

* **`FindAll`**: Returns all documents on collection
* **`FindOne`**: Returns `FirstOrDefault` result of `Find()`
* **`FindById`**: Returns `SingleOrDefault` result of `Find()` by using primary key `_id` index.
* **`Find`**: Return documents using `Query` builder or LINQ expression on collection.

`Find()` supports `Skip` and `Limit` parameters. These operations are used at the index level, so it’s more efficient than in LINQ to Objects.

`Find()` method returns an `IEnumerable` of documents. If you want do more complex filters, value as expressions, sorting or transforms results you can use LINQ to Objects.

Returning an `IEnumerable` your code still connected to datafile. Only when you finish consume all data, datafile will be disconected.

```
col.EnsureIndex(x => x.Name);

var result = col
    .Find(Query.EQ("Name", "John Doe")) // This filter is executed in LiteDB using index
    .Where(x => x.CreationDate >= x.DueDate.AddDays(-5)) // This filter is executed by LINQ to Object
    .OrderBy(x => x.Age)
    .Select(x => new 
    { 
        FullName = x.FirstName + " " + x.LastName, 
        DueDays = x.DueDate - x.CreationDate 
    }); // Transform
```

### Count() and Exists()

These two methods are useful because you can count documents (or check if a document exists) without deserializing the document.

```
// This way is more efficient
var count = collection.Count(Query.EQ("Name", "John Doe"));

// Than use Find + Count
var count = collection.Find(Query.EQ("Name", "John Doe")).Count();
```

* In the first count, LiteDB uses the index to search and count the number of index occurrences of “Name = John” without deserializing and mapping the document.
* If the `Name` field does not have an index, LiteDB will deserialize the document but will not run the mapper. Still faster than `Find().Count()`
* The same idea applies when using `Exists()`, which is again better than using `Count() >= 1`. Count needs to visit all matched results and `Exists()` stops on first match (similar to LINQ’s `Any` extension method).

### Min() and Max()

LiteDB uses a skip list implementation for indexes (See <Indexes>). Collections offer `Min` and `Max` index values. The implementation is:

* **`Min`** - Read head index node (MinValue BSON data type) and move to next node. This node is the lowest value in index. If index are empty, returns MinValue. Lowest value is not the first value!
* **`Max`** - Read tail index node (MaxValue BSON data type) and move to previous node. This node is the highest value on index. If index are empty, returns MaxValue. Highest value is not the last value!

Min/Max required a created index in field.

### LINQ expressions

Some LiteDB methods support predicates to allow you to easily query strongly typed documents. If you are working with `BsonDocument`, you need to use classic `Query` class methods.

```
col.Find(x => x.Name == "John Doe")
// Query.EQ("Name", "John Doe")

col.Find(x => x.Age > 30)
// Query.GT("Age", 30)

col.Find(x => x.Name.StartsWith("John") && x.Age > 30)
// Query.And(Query.StartsWith("Name", "John"), Query.GT("Age", 30))

// where PhoneNumbers is string[]
col.Find(x => x.PhoneNumbers.Contains("555-1234"))
// Query.EQ("PhoneNumbers", "555-1234")

// create index on Number inside phone array
col.EnsureIndex(x => x.Phones[0].Number); // ignore 0 index: it's just a syntax to access child
// db.EnsureIndex("col", "Phones[*].Number", false, "$.Phones[*].Number)

col.Find(x => x.Phones.Select(z => z.Number == "555-1234")) // another way to access child
// Query.EQ("Phones[*].Numbers", "555-1234")

col.Find(x => !(x.Age > 30))
// Query.Not(Query.GT("Age", 30))
```

* LINQ implementations are: `==, !=, >, >=, <, <=, StartsWith, Contains (string and IEnumerable), Equals, &&, ||, ! (not)`
* Property name support inner document field: `x => x.Name.Last == "Doe"`
* Behind the scene, LINQ expressions are converted to `Query` implementations using `QueryVisitor` class.

* [www.zerostatic.io](https://www.zerostatic.io)
