# Connection String

`LiteDatabase` accepts connection options in the `key1=value1;key2=value2` format. Input without `=` is treated as a filename. Input containing both `=` and `;`, or a single recognized option name followed by `=`, is parsed as a connection string. Quote a value with `"` or `'` when it contains a semicolon. For an arbitrary path that could be mistaken for options, set `ConnectionString.Filename` directly.

Option names are case-insensitive. Values are not universally case-insensitive: enum and Boolean values are parsed without regard to case, while values such as passwords and file paths retain their original text and casing.

## Options

| Key | Type | Description | Default value |
| --- | --- | --- | --- |
| `Filename` | string | Full or relative path to the datafile. Supports `:memory:` for an in-memory database and `:temp:` for a temporary database. **Required when opening a database.** | — |
| `Connection` | enum | Connection type: `Direct` or `Shared`. | `Direct` |
| `Password` | string | Encrypt the datafile using the supplied password. | null (no encryption) |
| `Initial Size` | size | Initial allocation for a new datafile. Accepts bytes or the `KB`, `MB`, `GB`, and `TB` suffixes. | `0` |
| `Memory Profile` | enum | Selects `Balanced`, `LowMemory`, or `Throughput` cache and transaction defaults. | `Balanced` |
| `Cache Size` | size | Overrides the profile's soft cache target. `0` selects the profile default. Values below 1 MB require an explicit unit. | `0` |
| `Transaction Pages` | int | Overrides the profile's cooperative transaction-page threshold. Must be greater than zero. | Profile default |
| `ReadOnly` | bool | Open the datafile in read-only mode. | `false` |
| `Upgrade` | bool | Upgrade an older datafile before opening it. | `false` |
| `Auto-Rebuild` | bool | Attempt to rebuild a datafile left in an invalid state by an interrupted close. | `false` |
| `Collation` | string | Set the collation when creating a datafile. | Current culture with `IgnoreCase` |

See [Memory profiles](../memory-profiles.md) for the profile defaults and sizing tradeoffs. For paths containing `=` or `;` and the exact option-classification rules, see [connection-string parsing compatibility](../connection-string-parsing.md).

### Connection Type

LiteDB offers 2 types of connections: `Direct` and `Shared`. This affects how the engine opens the data file.

* `Direct`: Opens the datafile in exclusive mode and keeps it open until `Dispose()`. No other process can open the file. This mode is recommended because it is faster and benefits from caching.
* `Shared`: Closes the datafile after each operation. Locks use a named `Mutex`. This mode is slower but allows multiple processes to open the same file.

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

Memory settings can be supplied in the same connection string:

```csharp
using var db = new LiteDatabase(
    "Filename=app.db;Memory Profile=LowMemory;Cache Size=16MB;Transaction Pages=512");
```


---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
