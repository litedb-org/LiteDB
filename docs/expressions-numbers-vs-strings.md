# Numbers vs. strings in expressions

A `BsonExpression` literal has a type, and the type decides what it can match.
Unquoted digits are a **number** (`Int32`, `Int64`, then `Decimal`, then `Double`
as the value grows); quoted text is a **string**. A number never equals a string,
whatever its length: `$.Name = 123` does not match `"123"`, and
`$.Name = 1111111111111111111111111` does not match `"1111111111111111111111111"`.
The query is valid and returns no rows.

This matters when a field holds digits stored as strings (long ids, phone
numbers, article codes). Compare such a field with a string:

```csharp
// best: the value travels as a parameter and keeps its CLR type
col.Find(BsonExpression.Create("$.Name = @0", value));    // value is a string

// same thing through the query API
col.Find(Query.EQ("Name", value));

// a quoted literal
col.Find("$.Name = '1111111111111111111111111'");
```

All three use an index on `Name` when one exists.

Prefer the parameter forms whenever the value comes from a user. Building
expression text by concatenation or interpolation lets the input change the
query (`' OR 1 = 1 OR $.Name = '` is a valid continuation of a quoted literal),
and it is also how a digits-only input silently turns into a number. A parameter
is never parsed as expression text.

The numeric types are one family: `5`, `5L`, `5.0` and `5m` compare equal and
find each other through an index. There is no implicit conversion between
numbers and strings, in either direction.

## A field that holds both numbers and strings

Convert explicitly:

```csharp
col.Find("STRING($.Name) = '123'");
```

`STRING(...)` is evaluated for every document, so this form cannot seek an index
on `$.Name`; it scans. Create an index on the converted expression
(`col.EnsureIndex("NameText", "STRING($.Name)")`) if the query is frequent, or
store the field with one type.

## Finding the mismatch: the plan warning

When an equality predicate (`=` or `IN`) is served by an index whose keys all
have another type than the value, the explain output says so:

```csharp
var plan = col.Query().Where("$.Name = 1111111111111111111111111").GetPlan();
// or: EXPLAIN SELECT $ FROM col WHERE $.Name = 1111111111111111111111111
```

```json
"index": {
    "name": "Name",
    "expr": "$.Name",
    "order": 1,
    "mode": "INDEX SEEK(Name = {\"$numberDecimal\":\"1111111111111111111111111\"})",
    "cost": 10,
    "warning": "index 'Name' holds String keys; the Decimal value cannot match"
}
```

The warning is a diagnostic only: the query runs exactly as before and returns
no rows. It is computed only when a plan is explained, from the first and the
last key of the index (keys are ordered by type before value, so two ends of the
same type leave no room for another one). Consequently there is no warning when

- the value's type can match (`5` against an index of `Double` keys is fine),
- the index holds more than one type, including `Null` keys of documents where
  the field is missing or null,
- the index is empty,
- the field has no index (only a scan could tell),
- the operator is not `=` or `IN`. Range operators compare across types by type
  order (`$.Name > 5` returns every string, because strings sort after numbers),
  so they are not reported.

For `IN`, the warning appears only when none of the listed values can match.
