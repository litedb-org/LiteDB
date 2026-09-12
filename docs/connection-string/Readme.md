# Connection String

LiteDatabase can be initialized using a connection string with the `key1=value1; key2=value2; ...` syntax. If there is no `=`, LiteDB assumes the string is the `Filename`. Quote values (`"` or `'`) when they contain reserved characters such as `;` or `=`. **Keys and values are case-insensitive.**

## Options

| Key | Type | Description | Default value |
| --- | --- | --- | --- |
| Filename | string | Full or relative path to the datafile. Supports `:memory:` for an in-memory database or `:temp:` for an on-disk temporary database (the file is deleted when the database closes). **Required.** | — |
| Connection | string | Connection type (“direct” or “shared”) | “direct” |
| Password | string | Encrypt the datafile with AES using a password. | null (no encryption) |
| InitialSize | string or long | Initial size for the datafile (strings support “KB”, “MB”, and “GB”). | 0 |
| ReadOnly | bool | Open the datafile in read-only mode. | false |
| Upgrade | bool | Upgrade the datafile if it is from an older version before opening it. | false |

### Connection Type

LiteDB offers 2 types of connections: `Direct` and `Shared`. This affects how the engine opens the data file.

* `Direct`: Opens the datafile in exclusive mode and keeps it open until `Dispose()`. No other process can open the file. This mode is recommended because it’s faster and benefits from caching.
* `Shared`: Closes the datafile after each operation. Locks use `Mutex`. This mode is slower but allows multiple processes to open the same file.

> The Shared mode only works in .NET implementations that provide named mutexes. Its multi-process capabilities will only work in platforms that implement named mutexes as system-wide mutexes.

## Example

### App.config

```xml
<connectionStrings>
    <add name="LiteDB" connectionString="Filename=C:\database.db;Password=1234" />
</connectionStrings>
```

### C#

```csharp
System.Configuration.ConfigurationManager.ConnectionStrings["LiteDB"].ConnectionString
```


---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
