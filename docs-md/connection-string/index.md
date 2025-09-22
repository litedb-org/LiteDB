Connection String - LiteDB :: A .NET embedded NoSQL database



[Fork me on GitHub](https://github.com/mbdavid/litedb)

* [HOME](/)
* [DOCS](/docs/)
* [API](/api/)
* [DOWNLOAD](https://www.nuget.org/packages/LiteDB/)

[![Logo](/images/logo_litedb.svg)](/)

[![Logo](/images/logo_litedb.svg)](/)

* [HOME](/)
* [DOCS](/docs/)
* [API](/api/)
* [DOWNLOAD](https://www.nuget.org/packages/LiteDB/)

#### Docs

* [Getting Started](/docs/getting-started/)
* [Data Structure](/docs/data-structure/)
* [Object Mapping](/docs/object-mapping/)
* [Collections](/docs/collections/)
* [BsonDocument](/docs/bsondocument/)
* [Expressions](/docs/expressions/)
* [DbRef](/docs/dbref/)
* [Connection String](/docs/connection-string/)
* [FileStorage](/docs/filestorage/)
* [Indexes](/docs/indexes/)
* [Encryption](/docs/encryption/)
* [Pragmas](/docs/pragmas/)
* [Collation](/docs/collation/)

# Connection String

LiteDatabase can be initialized using a string connection, with `key1=value1; key2=value2; ...` syntax. If there is no `=` in your connection string, LiteDB assume that your connection string contains only the `Filename`. Values can be quoted (`"` or `'`) if they contain special characters (like `;` or `=`). **Keys and values are case-insensitive.**

### Options

| Key | Type | Description | Default value |
| --- | --- | --- | --- |
| Filename | string | Full or relative path to the datafile. Supports `:memory:` for memory database or `:temp:` for in disk temporary database (file will deleted when database is closed) **[required]** | - |
| Connection | string | Connection type (“direct” or “shared”) | “direct” |
| Password | string | Encrypt (using AES) your datafile with a password | null (no encryption) |
| InitialSize | string or long | Initial size for the datafile (string suppoorts “KB”, “MB” and “GB”) | 0 |
| ReadOnly | bool | Open datafile in read-only mode | false |
| Upgrade | bool | Check if datafile is of an older version and upgrade it before opening | false |

#### Connection Type

LiteDB offers 2 types of connections: `Direct` and `Shared`. This affects how the engine opens the data file.

* `Direct`: The engine will open the datafile in exclusive mode and will keep it open until `Dispose()`. The datafile cannot be opened by another process. This is the recommended mode because it’s faster and cachable.
* `Shared`: The engine will be close the datafile after each operation. Locks are made using `Mutex`. This is more expensive but you can open same file from multiple processes.

> The Shared mode only works in .NET implementations that provide named mutexes. Its multi-process capabilities will only work in platforms that implement named mutexes as system-wide mutexes.

### Example

*App.config*

```
<connectionStrings>
    <add name="LiteDB" connectionString="Filename=C:\database.db;Password=1234" />
</connectionStrings>
```

*C#*

```
System.Configuration.ConfigurationManager.ConnectionStrings["LiteDB"].ConnectionString
```

* Made with ♥ by LiteDB team - [@mbdavid](https://twitter.com/mbdavid) - MIT License
