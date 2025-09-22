Collation - LiteDB :: A .NET embedded NoSQL database



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

# Collation

A collation is a special pragma (for more info, see [Pragmas](../pragmas)) that allows users to specify a culture and string compare options for a datafile.

Collation is a read-only pragma and can only be changed with a rebuild.

A collation is specified with the format `CultureName/CompareOption1[,CompareOptionN]`. For more info about compare options, check the [.NET documentation](https://docs.microsoft.com/en-us/dotnet/api/system.globalization.compareoptions).

Datafiles are always created with `CultureInfo.CurrentCulture` as their culture and with `IgnoreCase` as the compare option. The collation can be change by rebuilding the datafile.

Internally, the culture info is stored in the header of the datafile using its LCID value. Cultures with LCID value 4096 are not supported. When an unsupported culture is detected, the rebuild defaults to `CultureInfo.InvariantCulture`.

### Examples

* `rebuild {"collation": "en-US/None"};` rebuilds the datafile with the `en-US` culture and regular string comparison
* `rebuild {"collation": "en-GB/IgnoreCase"};` rebuilds the datafile with the `en-GB` culture and case-insensitive string comparison
* `rebuild {"collation": "pt-BR/IgnoreCase,IgnoreSymbols"};` rebuilds the datafile with the `pt-BR` culture and case-insensitive string comparison that also ignores symbols (white spaces, punctuation, math symbols etc.)

* Made with ♥ by LiteDB team - [@mbdavid](https://twitter.com/mbdavid) - MIT License
