Pragmas - LiteDB :: A .NET embedded NoSQL database



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

# Pragmas

In LiteDB v5, pragmas are variables that can alter the behavior of a datafile. They are stored in the header of the datafile.

| Name | Read-only | Data type | Description | Default value |
| --- | --- | --- | --- | --- |
| USER\_VERSION | no | int | Reserved for version control by the user. Does not affect the behavior of the datafile. | 0 |
| COLLATION | yes (can be changed with a rebuild) | string (internally stored as int) | Check [Collation](../collation). | `CurrentCulture` and `IgnoreCase` |
| TIMEOUT | no | int | Maximum amount of time (in seconds) that the engine waits for a shared resouce to be unlocked. | 60 |
| LIMIT\_SIZE | no | long | Maximum size (in bytes) that the datafile can grow to. Cannot be smaller than the current datafile size. Cannot be smaller than 4 pages (32768 bytes). | long.MaxValue |
| UTC\_DATE | no | bool | If `false`, dates are converted to local time on retrieval. Storage format is not affected (always in UTC). | false |
| CHECKPOINT | no | int | Maximum number of pages to be stored in the log before a soft checkpoint. If set to `0`, auto-checkpoint and shutdown checkpoint are disabled. | 1000 |

#### Examples

* `select pragmas from $database;` returns the pragmas in the current datafile
* `pragma USER_VERSION = 1;` sets USER\_VERSION to 1
* `pragma UTC_DATE = true;` sets UTC\_DATE to true

## Rebuild Options

Rebuild options are used to configure a rebuild.

| Name | Data type | Description | Default value |
| --- | --- | --- | --- |
| collation | string | Check [Collation](../collation). | null (will use `CurrentCulture` and `IgnoreCase` if null) |
| password | string | Defines the password for an encrypted datafile. | null (datafile will not be encrypted) |

If the `rebuild` command is issued without options, both are assumed to be null.

Rebuilds are also useful to defragment a datafile, making it smaller and faster to access.

* `rebuild;` rebuilds the database with the default collation and no password
* `rebuild {"collation": "en-GB/IgnoreCase"};` rebuilds the datafile with the `en-GB` culture and case-insensitive string comparison
* `rebuild {"collation": "pt-BR/None", "password" : "1234"};` rebuilds the datafile with the `pt-BR` culture, case-sensitive string comparison and sets the password to “1234”

* Made with ♥ by LiteDB team - [@mbdavid](https://twitter.com/mbdavid) - MIT License
