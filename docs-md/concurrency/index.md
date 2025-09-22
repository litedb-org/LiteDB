Concurrency - Hugo Whisper Theme



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

# Concurrency

LiteDB v4 supports both thread-safe and process-safe:

* You can create a new instance of `LiteRepository`, `LiteDatabase` or `LiteEngine` in each use (process-safe)
* You can share a single `LiteRepository`, `LiteDatabase` or `LiteEngine` instance across your threads (thread-safe)

In first option (process safe), you will always work disconnected from the datafile. Each use will open datafile, lock file (read or write mode), do your operation and then close datafile. Locks are implemented using `FileStream.Lock` for both read/write mode. It’s very important in this way to always use `using` statement to close datafile.

In second option (thread-safe), LiteDB controls concurrency using `ReaderWriterLockSlim` .NET class. With this class it’s possible manage multiple reads and an exclusive write. All threads share same instance and each method control concurrency.

## Recommendation

Single instance (second option) is much faster than multi instances. In multi instances environment, each instance must do expensive data file operations: open, lock, unlock, read, close. Also, each instance has its own cache control and, if used only for a single operation, will discard all cached pages on close of datafile. In single instance, all pages in cache are shared between all read threads.

If your application works in a single process (like mobile apps, asp.net websites) prefer to use a single database instance and share across all threads.

You can use `Exclusive` mode (in connection string). Using exclusive mode, datafile will avoid checking header page for any any external change. Also, exclusive mode do not use Lock/Unlock in file, only in memory (using `ReaderWriterLockSlim` class).

* [www.zerostatic.io](https://www.zerostatic.io)
