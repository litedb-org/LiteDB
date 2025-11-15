using System;
using System.Collections.Generic;
using System.IO;
using LiteDB.Engine;

namespace LiteDB
{
    /// <summary>
    /// Represents the main database interface for LiteDB operations, providing access to collections, file storage, transactions, and database management.
    /// </summary>
    /// <remarks>
    /// This is the primary interface for interacting with a LiteDB database. It provides strongly-typed collections,
    /// SQL command execution, transaction management, and database maintenance operations.
    /// <para>Instances are typically created using the <see cref="LiteDatabase"/> class.</para>
    /// </remarks>
    public interface ILiteDatabase : IDisposable
    {
        /// <summary>
        /// Gets the <see cref="BsonMapper"/> instance used by this database for object-to-document mapping.
        /// </summary>
        /// <remarks>
        /// This may be a custom mapper instance or <see cref="BsonMapper.Global"/>. Use this mapper to configure
        /// entity mappings, custom serializers, and field name resolution.
        /// </remarks>
        BsonMapper Mapper { get; }

        /// <summary>
        /// Gets the default file storage for storing files and streams within the database using string file IDs.
        /// </summary>
        /// <remarks>
        /// Uses the default <c>_files</c> and <c>_chunks</c> collections. For custom file ID types or collection names,
        /// use <see cref="GetStorage{TFileId}"/>.
        /// </remarks>
        ILiteStorage<string> FileStorage { get; }

        /// <summary>
        /// Gets a strongly-typed collection for the specified entity type. Creates the collection if it does not exist.
        /// </summary>
        /// <typeparam name="T">The entity type for documents in this collection.</typeparam>
        /// <param name="name">The collection name (case-insensitive).</param>
        /// <param name="autoId">The auto-ID generation strategy when documents have no ID field.</param>
        /// <returns>A <see cref="ILiteCollection{T}"/> instance for accessing the collection.</returns>
        ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId);

        /// <summary>
        /// Gets a strongly-typed collection using the type name as the collection name.
        /// </summary>
        /// <typeparam name="T">The entity type for documents in this collection.</typeparam>
        /// <returns>A <see cref="ILiteCollection{T}"/> instance for accessing the collection.</returns>
        /// <remarks>
        /// The collection name is determined by <see cref="BsonMapper.ResolveCollectionName"/>. Creates the collection if it does not exist.
        /// </remarks>
        ILiteCollection<T> GetCollection<T>();

        /// <summary>
        /// Gets a strongly-typed collection using the type name as the collection name with a specified auto-ID strategy.
        /// </summary>
        /// <typeparam name="T">The entity type for documents in this collection.</typeparam>
        /// <param name="autoId">The auto-ID generation strategy when documents have no ID field.</param>
        /// <returns>A <see cref="ILiteCollection{T}"/> instance for accessing the collection.</returns>
        /// <remarks>
        /// The collection name is determined by <see cref="BsonMapper.ResolveCollectionName"/>. Creates the collection if it does not exist.
        /// </remarks>
        ILiteCollection<T> GetCollection<T>(BsonAutoId autoId);

        /// <summary>
        /// Gets a collection using <see cref="BsonDocument"/> for untyped document access. Creates the collection if it does not exist.
        /// </summary>
        /// <param name="name">The collection name (case-insensitive).</param>
        /// <param name="autoId">The auto-ID generation strategy when documents have no <c>_id</c> field.</param>
        /// <returns>A <see cref="ILiteCollection{BsonDocument}"/> instance for accessing the collection.</returns>
        ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId);

        /// <summary>
        /// Begins a new transaction on the current thread.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if a new transaction was created; <see langword="false"/> if the current thread already has an active transaction.
        /// </returns>
        /// <remarks>
        /// Transactions are per-thread. Only one transaction can be active per thread at a time.
        /// Use <see cref="Commit"/> or <see cref="Rollback"/> to complete the transaction.
        /// </remarks>
        bool BeginTrans();

        /// <summary>
        /// Commits the current transaction, persisting all changes made within the transaction.
        /// </summary>
        /// <returns><see langword="true"/> if the transaction was committed; <see langword="false"/> if no transaction was active.</returns>
        bool Commit();

        /// <summary>
        /// Rolls back the current transaction, discarding all changes made within the transaction.
        /// </summary>
        /// <returns><see langword="true"/> if the transaction was rolled back; <see langword="false"/> if no transaction was active.</returns>
        bool Rollback();

        /// <summary>
        /// Gets a file storage instance with a custom file ID type and custom collection names.
        /// </summary>
        /// <typeparam name="TFileId">The type to use for file identifiers.</typeparam>
        /// <param name="filesCollection">The collection name for file metadata. Default is <c>_files</c>.</param>
        /// <param name="chunksCollection">The collection name for file chunks. Default is <c>_chunks</c>.</param>
        /// <returns>A <see cref="ILiteStorage{TFileId}"/> instance for file operations.</returns>
        /// <remarks>
        /// LiteDB supports multiple file storage instances using different collection names, allowing for separate file storage areas within the same database.
        /// </remarks>
        ILiteStorage<TFileId> GetStorage<TFileId>(string filesCollection = "_files", string chunksCollection = "_chunks");

        /// <summary>
        /// Gets the names of all collections in the database.
        /// </summary>
        /// <returns>An enumerable collection of collection names.</returns>
        IEnumerable<string> GetCollectionNames();

        /// <summary>
        /// Checks whether a collection exists in the database.
        /// </summary>
        /// <param name="name">The collection name to check (case-insensitive).</param>
        /// <returns><see langword="true"/> if the collection exists; otherwise, <see langword="false"/>.</returns>
        bool CollectionExists(string name);

        /// <summary>
        /// Drops a collection, removing all documents and indexes.
        /// </summary>
        /// <param name="name">The collection name to drop (case-insensitive).</param>
        /// <returns><see langword="true"/> if the collection was dropped; <see langword="false"/> if the collection does not exist.</returns>
        bool DropCollection(string name);

        /// <summary>
        /// Renames a collection.
        /// </summary>
        /// <param name="oldName">The current collection name (case-insensitive).</param>
        /// <param name="newName">The new collection name (case-insensitive).</param>
        /// <returns>
        /// <see langword="true"/> if the collection was renamed; <see langword="false"/> if <paramref name="oldName"/> does not exist
        /// or <paramref name="newName"/> already exists.
        /// </returns>
        bool RenameCollection(string oldName, string newName);

        /// <summary>
        /// Executes SQL commands from a text reader and returns the results as a data reader.
        /// </summary>
        /// <param name="commandReader">A <see cref="TextReader"/> containing the SQL commands to execute.</param>
        /// <param name="parameters">Optional parameters for the SQL commands. Default is <see langword="null"/>.</param>
        /// <returns>A <see cref="IBsonDataReader"/> for reading the query results.</returns>
        IBsonDataReader Execute(TextReader commandReader, BsonDocument parameters = null);

        /// <summary>
        /// Executes a SQL command string and returns the results as a data reader.
        /// </summary>
        /// <param name="command">The SQL command string to execute.</param>
        /// <param name="parameters">Optional named parameters for the SQL command. Default is <see langword="null"/>.</param>
        /// <returns>A <see cref="IBsonDataReader"/> for reading the query results.</returns>
        IBsonDataReader Execute(string command, BsonDocument parameters = null);

        /// <summary>
        /// Executes a SQL command string with positional parameters and returns the results as a data reader.
        /// </summary>
        /// <param name="command">The SQL command string to execute, using <c>@0</c>, <c>@1</c>, etc. for parameters.</param>
        /// <param name="args">Positional parameter values to substitute into the command.</param>
        /// <returns>A <see cref="IBsonDataReader"/> for reading the query results.</returns>
        IBsonDataReader Execute(string command, params BsonValue[] args);

        /// <summary>
        /// Performs a database checkpoint, copying all committed transactions from the log file to the data file.
        /// </summary>
        /// <remarks>
        /// Checkpointing is normally automatic based on <see cref="CheckpointSize"/>, but can be triggered manually
        /// for immediate persistence or before critical operations.
        /// </remarks>
        void Checkpoint();

        /// <summary>
        /// Rebuilds the entire database to remove unused pages and reduce the data file size.
        /// </summary>
        /// <param name="options">Optional rebuild options. Default is <see langword="null"/> (use default options).</param>
        /// <returns>The number of bytes reduced from the data file.</returns>
        /// <remarks>
        /// Rebuilding is a maintenance operation that compacts the database by removing empty pages and reorganizing data.
        /// This operation requires exclusive access and may take significant time for large databases.
        /// </remarks>
        long Rebuild(RebuildOptions options = null);

        /// <summary>
        /// Gets the value of an internal engine variable.
        /// </summary>
        /// <param name="name">The variable name (case-insensitive).</param>
        /// <returns>The current value of the variable as a <see cref="BsonValue"/>.</returns>
        BsonValue Pragma(string name);

        /// <summary>
        /// Sets a new value for an internal engine variable.
        /// </summary>
        /// <param name="name">The variable name (case-insensitive).</param>
        /// <param name="value">The new value to set.</param>
        /// <returns>The previous value of the variable as a <see cref="BsonValue"/>.</returns>
        BsonValue Pragma(string name, BsonValue value);

        /// <summary>
        /// Gets or sets the user-defined version number for the database.
        /// </summary>
        /// <remarks>
        /// Use this property to track your application's database schema version for migration and upgrade scenarios.
        /// This value is stored in the database and persists across sessions.
        /// </remarks>
        int UserVersion { get; set; }

        /// <summary>
        /// Gets or sets the timeout for acquiring locks during transactions.
        /// </summary>
        /// <remarks>
        /// This timeout applies when waiting for locks to be released by other transactions. If a lock cannot be
        /// acquired within this timespan, a <see cref="LiteException"/> with error code <see cref="LiteException.LOCK_TIMEOUT"/> is thrown.
        /// </remarks>
        TimeSpan Timeout { get; set; }

        /// <summary>
        /// Gets or sets whether dates are deserialized in UTC timezone or local timezone. Default is local.
        /// </summary>
        /// <remarks>
        /// When <see langword="true"/>, dates are returned in UTC. When <see langword="false"/>, dates are converted to local timezone.
        /// This setting affects all date deserialization in the database.
        /// </remarks>
        bool UtcDate { get; set; }

        /// <summary>
        /// Gets or sets the maximum database size in bytes.
        /// </summary>
        /// <remarks>
        /// The new value must be equal to or larger than the current database size. Setting this limit prevents the
        /// database from growing beyond the specified size. Use 0 for no limit (default).
        /// </remarks>
        long LimitSize { get; set; }

        /// <summary>
        /// Gets or sets the auto-checkpoint threshold in pages (8 KB per page).
        /// </summary>
        /// <remarks>
        /// When the log file reaches this many pages, an automatic checkpoint is triggered to copy changes from the log file
        /// to the data file. Set to 0 for manual-only checkpointing (no automatic checkpoint or checkpoint on dispose).
        /// Default is 1000 pages (8 MB).
        /// </remarks>
        int CheckpointSize { get; set; }

        /// <summary>
        /// Gets the collation used for string comparisons and sorting in this database.
        /// </summary>
        /// <remarks>
        /// The collation is set when the database is created and can only be changed through the rebuild process.
        /// </remarks>
        Collation Collation { get; }
    }
}