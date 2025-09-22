Overview - LiteDB :: A .NET embedded NoSQL database



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

Overview

# Overview

## LiteDB v5 - A .NET NoSQL Document Store in a single data file

## [Getting Started](/docs/getting-started/)

LiteDB is a simple, fast and lightweight embedded .NET document database. LiteDB was inspired by the MongoDB database and its API is very …

## [Data Structure](/docs/data-structure/)

LiteDB stores data as documents, which are JSON-like objects containing key-value pairs. Documents are a schema-less data structure. Each …

## [Object Mapping](/docs/object-mapping/)

The LiteDB mapper converts POCO classes documents. When you get a ILiteCollection instance from LiteDatabase.GetCollection, T will be your …

## [Collections](/docs/collections/)

Documents are stored and organized in collections. LiteCollection is a generic class that is used to manage collections in LiteDB. Each …

## [BsonDocument](/docs/bsondocument/)

The BsonDocument class is LiteDB’s implementation of documents. Internally, a BsonDocument stores key-value pairs in a Dictionary.
var …

## [Expressions](/docs/expressions/)

Expressions are path or formulas to access and modify the data inside a document. Based on the concept of JSON path …

## [DbRef](/docs/dbref/)

LiteDB is a document database, so there is no JOIN between collections. You can use embedded documents (sub-documents) or create a reference …

## [Connection String](/docs/connection-string/)

LiteDatabase can be initialized using a string connection, with key1=value1; key2=value2; ... syntax. If there is no = in your connection …

## [FileStorage](/docs/filestorage/)

To keep its memory profile slim, LiteDB limits the size of a documents to 1MB. For most documents, this is plenty. However, 1MB is too small …

## [Indexes](/docs/indexes/)

LiteDB improves search performance by using indexes on document fields or expressions. Each index storess the value of a specific expression …

## [Encryption](/docs/encryption/)

LiteDB uses salted AES (as defined by RFC 2898) as its encryption. This is implemented by the Rfc2898DeriveBytes class.
The Aes object used …

## [Pragmas](/docs/pragmas/)

In LiteDB v5, pragmas are variables that can alter the behavior of a datafile. They are stored in the header of the datafile.
Name …

## [Collation](/docs/collation/)

A collation is a special pragma (for more info, see Pragmas) that allows users to specify a culture and string compare options for a …

* Made with ♥ by LiteDB team - [@mbdavid](https://twitter.com/mbdavid) - MIT License
