ChangeLog - Hugo Whisper Theme



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

# ChangeLog

## New Features

* Add support to NETStandard 2.0 (with support to `Shared` mode)
* New document `Expression` parser/executor - see [Expression Wiki](https://github.com/mbdavid/LiteDB/wiki/Expressions)
* Support index creation with expressions

  ```
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

* [www.zerostatic.io](https://www.zerostatic.io)
