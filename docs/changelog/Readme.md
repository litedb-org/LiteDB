# ChangeLog

## New Features

* Add support to NETStandard 2.0 (with support to `Shared` mode)
* New document `Expression` parser/executor - see [Expression Wiki](https://github.com/mbdavid/LiteDB/wiki/Expressions)
* Support index creation with expressions

```csharp
  col.EnsureIndex(x => x.Name, "LOWER($.Name)");
  col.EnsureIndex("GrandTotal", "SUM($.Items[*].Qtd * $.Items[*].Price)");
```

  + Query with `Include` it´s supported in Engine level with ANY nested includes
    `C#
    col.Include(x => x.Users)
    .Include(x => x.Users[0].Address)
    .Include(x => x.Users[0].Address.City)
    .Find(...)`
* Support complex Linq queries using `LinqQuery` compiler (works as linq to object)

  + `col.Find(x => x.Name == "John" && x.Items.Length.ToString().EndsWith == "0")`
* Better execution plan (with debug info) in multi query statements
* No more external journal file - use same datafile to store temporary data
* Fixed concurrency problems (keeps thread/process safe)
* Convert `Query.And` to `Query.Between` when possible
* Add support to `Query.Between` open/close interval
* **Same datafile from LiteDB `v3` (no upgrade needed)**

## Shell

* New UPDATE/SELECT statements in shell
* Shell commands parser/executor are back into LiteDB.dll
* Better shell error messages in parser with position in error
* Print query execution plan in debug
  `(Seek([Age] > 10) and Filter([Name] startsWith "John"))`
  (preparing to new visual LiteDB database management tool)

## Breaking changes

* Remove transactions
* Remove auto-id register function for custom type
* Remove index definitions on mapper (fluent/attribute)
* Remove auto create index on query execution. If the index is not found do full scan search (use `EnsureIndex` on initialize database)

---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
