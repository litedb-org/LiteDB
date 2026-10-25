# Recovering documents with an out-of-range date (#2930)

LiteDB never writes a `DateTime` outside `DateTime.MinValue`..`DateTime.MaxValue`, but a damaged data file can contain
one. Such a value used to make every read of its collection throw, so the data could neither be exported nor repaired.

## What happens now

A stored date beyond the representable range is read as the nearest representable value: `DateTime.MinValue` for a value
below the range and `DateTime.MaxValue` for one above it. The document loads, every other field is intact, and the
collection can be read, exported to JSON and queried. Valid dates are read exactly as before; nothing changes on write.

The clamp is silent: a loaded `DateTime.MinValue` / `DateTime.MaxValue` in a field that should hold a real date is the
sign of a damaged value.

## Repairing the documents

```csharp
var col = db.GetCollection<Material>("materials");

foreach (var doc in col.Find(x => x.CreateDate == DateTime.MinValue || x.CreateDate == DateTime.MaxValue).ToList())
{
    doc.CreateDate = DateTime.UtcNow;    // or whatever placeholder fits
    col.Update(doc);                     // rewrites the document and its index keys with a valid date
}
```

Use a full scan for this (the query above on a non-indexed field, or `FindAll()` filtered in memory). If the damaged
field is **indexed** and the index key itself is damaged too, that index is out of order around the damaged entry until
the document is rewritten or deleted: full scans, `FindById`, update and delete work, but a seek or range query on that
index can miss neighbouring documents. Rewriting or deleting the damaged documents, or `db.Rebuild()`, puts the index
back in order.

## If a file cannot be read at all

`db.Rebuild()` (with the password in the connection string for an encrypted file) copies every readable document into a
new data file and keeps the old one as a `-backup` file. With `new RebuildOptions { IncludeErrorReport = true }` the
documents it could not read are listed in the `_rebuild_errors` collection.
