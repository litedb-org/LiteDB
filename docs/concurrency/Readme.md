# Concurrency

LiteDB v4 supports both thread-safe and process-safe patterns:

* You can create a new instance of `LiteRepository`, `LiteDatabase` or `LiteEngine` in each use (process-safe)
* You can share a single `LiteRepository`, `LiteDatabase` or `LiteEngine` instance across your threads (thread-safe)

With the first option (process-safe), each operation opens the datafile, acquires a read or write lock, performs the work, and then closes the file. Locks are implemented using `FileStream.Lock` for both read and write modes. Always wrap the database in a `using` statement to ensure the file is closed promptly.

With the second option (thread-safe), LiteDB controls concurrency using the .NET `ReaderWriterLockSlim` class. This allows many concurrent readers and a single exclusive writer. All threads share the same database instance, and each method coordinates access.

## Recommendation

Running a single shared instance is significantly faster than spinning up multiple instances. Each separate instance must perform expensive operations—open, lock, unlock, read, and close. Each instance also manages its own cache; if it handles only one operation, it will discard cached pages immediately. A single instance keeps pages cached and shares them across all reader threads.

If your application runs in a single process (mobile apps, ASP.NET sites), prefer a single database instance shared across all threads.

You can also enable `Exclusive` mode in the connection string. In exclusive mode, LiteDB does not check the header page for external changes and relies on in-memory locking only (via `ReaderWriterLockSlim`).

---

*Made with ♥ by the LiteDB team – [@mbdavid](https://twitter.com/mbdavid) – MIT License.*
