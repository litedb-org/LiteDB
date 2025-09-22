How LiteDB Works - Hugo Whisper Theme



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

# How LiteDB Works

### File Format

LiteDB is a single file database. But databases have many different types of information, like indexes, collections, documents. To manage this, LiteDB implements database pages concepts. Page is a block of same information type and has 4096 bytes. Page is the minimum read/write operation on disk file. There are 6 page types:

* **Header Page**: Contains database information, like file version, data file size and pointer to free list pages. Is the first page on database (`PageID` = 0).
* **Collection Page**: Each collection use one page and hold all collection information, like name, indexes, pointers and options. All collections are connected each others by a double linked list.
* **Index Page**: Used to hold index nodes. LiteDB implement skip list indexes, so each node has one key and levels link pointers to others index nodes.
* **Data Page**: Data page contains data blocks. Each data block represent an document serialized in BSON format. If a document is bigger than one page, data block use a link pointer to an extended page.
* **Extend Page**: Big documents that need more than one page, are serialized in multiples extended pages. Extended pages are double linked to create a single data segment that to store documents. Each extend page contains only one document or a chunk of a document.
* **Empty Page**: When a page is excluded becomes a empty page. Empty pages will be use on next page request (for any kind of page).

Each page has a own header and content. Header is used to manage common data structure like PageID, PageType, Free Space. Content are implement different on each page type.

#### Page free space

Index pages and data pages contains a collection of elements (index nodes and data blocks). This pages can store data and keep with available space to more. To hold this free space on each page type, LiteDB implements free list pages.

Free list are a double linked list, sorted by available space. When database need a page to store data use this list to search first available page. After this, `PagerService` fix page order or remove from free list if there is no more space on page.

To create near data related, each collection contains an data free list. So, in a data page, all data blocks are of same collection. The same occurs in indexes. Each index (on each collection) contains your own free list. This solution consume more disk space but are much faster to read/write operations because data are related and near one with other. If you get all documents in a collection, database needs read less pages on disk.

### Limits

* Collection Name:
  + Pattern: `A-Z`, `_-`, `0-9`
  + Maxlength of 60 chars
  + Case insensitive
* Index Name:
  + Pattern: `A-Z`, `_-`, `0-9` (`.` for nested document)
  + Maxlength of 60 chars
  + Case sensitive
* BsonDocument Key Name:
  + `A-Z`, `_-`, `0-9`
  + Case sensitive
* FileStorage FileId:
  + Pattern: same used in file system path/file.
  + Case sensitive
* Collections: 3000 bytes to all collections names (each collection has 8 bytes of overhead)
* Documents per collections: `UInt.MaxValue`
* FileStorage Max File Length: 2Gb per file
* Index key size: 512 bytes after BSON serialization
* BSON document size: no limit (but highly recommended to keep as little as possible, under 200kb)
* Nested Depth for BSON Documents: 20
* Page Size: 4096 bytes
* DataPage Reserved Bytes: 2039 bytes (like PCT FREE)
* IndexPage Reserved Bytes: 100 bytes (like PCT FREE)
* Database Max Length: In theory, `UInt.MaxValue` \* PageSize (4096) = 16TB ~ Too big!

* [www.zerostatic.io](https://www.zerostatic.io)
