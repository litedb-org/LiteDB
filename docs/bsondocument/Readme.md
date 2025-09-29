# BsonDocument

The `BsonDocument` class is LiteDB’s implementation of documents. Internally, a `BsonDocument` stores key-value pairs in a `Dictionary<string, BsonValue>`.

```csharp
var customer = new BsonDocument();
customer["_id"] = ObjectId.NewObjectId();
customer["Name"] = "John Doe";
customer["CreateDate"] = DateTime.Now;
customer["Phones"] = new BsonArray { "8000-0000", "9000-000" };
customer["IsActive"] = true;
customer["IsAdmin"] = new BsonValue(true);
customer["Address"] = new BsonDocument
{
    ["Street"] = "Av. Protasio Alves"
};
customer["Address"]["Number"] = "1331";
```

LiteDB supports documents up to 16MB after BSON serialization.

## Document keys

* Keys are case-insensitive
* Duplicate keys are not allowed
* LiteDB keeps the original key order, including mapped classes. The only exception is for `_id` field, which will always be the first field.

## Document values

* Values can be any BSON value data type: Null, Int32, Int64, Decimal, Double, String, Embedded Document, Array, Binary, ObjectId, Guid, Boolean, DateTime, MinValue, MaxValue
* When a field is indexed, the value must occupy less than 1024 bytes after BSON serialization.
* `_id` field cannot be: `Null`, `MinValue` or `MaxValue`
* `_id` is unique indexed field, so value must occupy less than 1024 bytes

## Related .NET types

* `BsonValue`
  - Holds any BSON data type, including null, arrays, or documents.
  - Provides implicit constructors for supported .NET data types.
  - Is immutable.
  - Exposes the underlying .NET value via the `RawValue` property.
* `BsonArray`
  - Supports `IEnumerable<BsonValue>`.
  - Allows array items with different BSON types.
* `BsonDocument`
  - Missing fields always return `BsonValue.Null`.

```csharp
// Testing BSON value data type
if(customer["Name"].IsString) { ... }

// Helper to get .NET type
string str = customer["Name"].AsString;
```

To use other .NET data types you need a custom `BsonMapper` class.


---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
