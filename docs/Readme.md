# Overview

LiteDB v5 is a single-file, serverless NoSQL database for .NET. It stores JSON-like documents, offers LINQ-friendly queries, and is designed to be fast, lightweight, and easy to embed in desktop, web, or mobile applications.

## Documentation map

Use the following guides to explore LiteDB's feature set:

| Topic | Summary |
| --- | --- |
| [Getting Started](../getting-started/) | Install LiteDB, connect to a database, and perform basic CRUD operations with strongly typed collections. |
| [Data Structure](../data-structure/) | Learn how LiteDB organizes data internally, including pages, collections, and how documents are persisted on disk. |
| [Object Mapping](../object-mapping/) | Configure the mapper that turns your POCO classes into BSON documents, including custom type conversions and attributes. |
| [Collections](../collections/) | Manage collections, control identity keys, and work with typed and dynamic documents. |
| [BsonDocument](../bsondocument/) | Work directly with `BsonDocument` when you need dynamic schemas or advanced document manipulation. |
| [Expressions](../expressions/) | Compose expressions to filter, project, and update documents with LiteDB's JSON-path-inspired language. |
| [DbRef](../dbref/) | Model relationships across collections with references while keeping documents denormalized when appropriate. |
| [Connection String](../connection-string/) | Configure the LiteDB engine with connection string options such as file paths, timeouts, and journaling. |
| [FileStorage](../filestorage/) | Store and stream files alongside your documents with the GridFS-inspired FileStorage API. |
| [Indexes](../indexes/) | Accelerate queries with single-field, compound, and expression-based indexes, including multikey support. |
| [Encryption](../encryption/) | Protect data files using password-based AES encryption and understand how keys are derived. |
| [Pragmas](../pragmas/) | Tune engine behavior at runtime with pragmas such as size limits, timeouts, and default collations. |
| [Collation](../collation/) | Control string comparison rules by customizing collations per database. |
| [Queries](../queries/) | Explore the query API, including typed queries, aggregation, and filtering across indexes. |
| [Concurrency](../concurrency/) | Understand how LiteDB handles locking, transactions, and multi-threaded access. |
| [How LiteDB Works](../how-litedb-works/) | Dive into the engine design, including the page allocator, WAL file, and recovery process. |
| [Repository Pattern](../repository-pattern/) | Wrap LiteDB with repositories for domain-driven designs and testability. |
| [Versioning](../versioning/) | See how LiteDB evolves, how semantic versioning applies, and where to find upgrade notes. |
| [Changelog](../changelog/) | Review notable changes across releases. |

---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
