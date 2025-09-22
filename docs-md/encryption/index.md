Encryption - LiteDB :: A .NET embedded NoSQL database



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

# Encryption

LiteDB uses salted AES (as defined by [RFC 2898](https://tools.ietf.org/html/rfc2898)) as its encryption. This is implemented by the `Rfc2898DeriveBytes` class.

The `Aes` object used for cryptography is initialized with `PaddingMode.None` and `CipherMode.ECB`.

The password for an encrypted datafile is defined in the connection string (for more info, check [Connection String](../connection-string)). The password can only be changed or removed by rebuilding the datafile (for more info, check Rebuild Options in [Pragmas](../pragmas#rebuildOptions)).

* Made with ♥ by LiteDB team - [@mbdavid](https://twitter.com/mbdavid) - MIT License
