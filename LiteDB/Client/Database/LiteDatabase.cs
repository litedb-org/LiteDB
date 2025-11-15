using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents a connection to a LiteDB database, providing methods for managing collections, transactions, file storage, and executing SQL commands.
    /// </summary>
    /// <remarks>
    /// <para>Use this class to interact with a LiteDB database file or stream. The database supports NoSQL operations with BSON document storage.</para>
    /// <para>The LiteDatabase instance is thread-safe and supports concurrent read operations. Write operations are automatically serialized through internal locking mechanisms.</para>
    /// <para>Call <see cref="Dispose()"/> when finished to release resources and ensure all changes are committed.</para>
    /// </remarks>
    public partial class LiteDatabase : ILiteDatabase
    {
        #region Properties

        private readonly ILiteEngine _engine;
        private readonly BsonMapper _mapper;
        private readonly bool _disposeOnClose;
        private readonly int? _checkpointOverride;

        /// <summary>
        /// Gets the current instance of <see cref="BsonMapper"/> used in this database instance (may be <see cref="BsonMapper.Global"/>).
        /// </summary>
        public BsonMapper Mapper => _mapper;

        #endregion

        #region Ctor

        /// <summary>
        /// Initializes a new instance of the <see cref="LiteDatabase"/> class using a connection string for a file-based database.
        /// </summary>
        /// <param name="connectionString">The connection string specifying database settings.</param>
        /// <param name="mapper">Optional custom <see cref="BsonMapper"/> instance. If <see langword="null"/>, uses <see cref="BsonMapper.Global"/>.</param>
        public LiteDatabase(string connectionString, BsonMapper mapper = null)
            : this(new ConnectionString(connectionString), mapper)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LiteDatabase"/> class using a <see cref="ConnectionString"/> for a file-based database.
        /// </summary>
        /// <param name="connectionString">The <see cref="ConnectionString"/> object containing database settings.</param>
        /// <param name="mapper">Optional custom <see cref="BsonMapper"/> instance. If <see langword="null"/>, uses <see cref="BsonMapper.Global"/>.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="connectionString"/> is <see langword="null"/>.</exception>
        public LiteDatabase(ConnectionString connectionString, BsonMapper mapper = null)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

            _engine = connectionString.CreateEngine();
            _mapper = mapper ?? BsonMapper.Global;
            _disposeOnClose = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LiteDatabase"/> class using a generic <see cref="Stream"/> implementation (typically <see cref="MemoryStream"/>).
        /// </summary>
        /// <param name="stream">The stream to use for data storage.</param>
        /// <param name="mapper">Optional custom <see cref="BsonMapper"/> instance. If <see langword="null"/>, uses <see cref="BsonMapper.Global"/>.</param>
        /// <param name="logStream">Optional stream for write-ahead logging.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
        public LiteDatabase(Stream stream, BsonMapper mapper = null, Stream logStream = null)
        {
            var settings = new EngineSettings
            {
                DataStream = stream ?? throw new ArgumentNullException(nameof(stream)),
                LogStream = logStream
            };

            _engine = new LiteEngine(settings);
            _mapper = mapper ?? BsonMapper.Global;
            _disposeOnClose = true;

            if (logStream == null && stream is not MemoryStream)
            {
                if (!stream.CanWrite)
                {
                    // Read-only streams cannot participate in eager checkpointing because the process
                    // writes pages back to the underlying data stream immediately.
        }
                else
                {
                    // Without a dedicated log stream the WAL lives purely in memory; force
                    // checkpointing to ensure commits reach the underlying data stream.
                    var originalCheckpointSize = _engine.Pragma(Pragmas.CHECKPOINT);

                    if (originalCheckpointSize != 1)
                    {
                        _engine.Pragma(Pragmas.CHECKPOINT, 1);
                        _checkpointOverride = originalCheckpointSize;
                    }
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="LiteDatabase"/> class using a pre-existing <see cref="ILiteEngine"/> instance.
        /// </summary>
        /// <param name="engine">The engine instance to use.</param>
        /// <param name="mapper">Optional custom <see cref="BsonMapper"/> instance. If <see langword="null"/>, uses <see cref="BsonMapper.Global"/>.</param>
        /// <param name="disposeOnClose">If <see langword="true"/>, the engine will be disposed when this database instance is disposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="engine"/> is <see langword="null"/>.</exception>
        public LiteDatabase(ILiteEngine engine, BsonMapper mapper = null, bool disposeOnClose = true)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _mapper = mapper ?? BsonMapper.Global;
            _disposeOnClose = disposeOnClose;
        }

        #endregion

        #region Collections

        /// <summary>
        /// Gets a strongly-typed collection using an entity class. Creates the collection if it does not exist.
        /// </summary>
        /// <typeparam name="T">The entity type for the collection.</typeparam>
        /// <param name="name">The collection name (case insensitive). If <see langword="null"/>, uses the type name resolved by <see cref="BsonMapper.ResolveCollectionName"/>.</param>
        /// <param name="autoId">The auto-ID strategy to use when a document has no <c>_id</c> field. Default is <see cref="BsonAutoId.ObjectId"/>.</param>
        /// <returns>An <see cref="ILiteCollection{T}"/> instance for the specified collection.</returns>
        public ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            return new LiteCollection<T>(name, autoId, _engine, _mapper);
        }

        /// <summary>
        /// Gets a strongly-typed collection using the type name as the collection name (resolved by <see cref="BsonMapper.ResolveCollectionName"/>).
        /// </summary>
        /// <typeparam name="T">The entity type for the collection.</typeparam>
        /// <returns>An <see cref="ILiteCollection{T}"/> instance for the specified collection.</returns>
        public ILiteCollection<T> GetCollection<T>()
        {
            return this.GetCollection<T>(null);
        }

        /// <summary>
        /// Gets a strongly-typed collection using the type name as the collection name (resolved by <see cref="BsonMapper.ResolveCollectionName"/>).
        /// </summary>
        /// <typeparam name="T">The entity type for the collection.</typeparam>
        /// <param name="autoId">The auto-ID strategy to use when a document has no <c>_id</c> field.</param>
        /// <returns>An <see cref="ILiteCollection{T}"/> instance for the specified collection.</returns>
        public ILiteCollection<T> GetCollection<T>(BsonAutoId autoId)
        {
            return this.GetCollection<T>(null, autoId);
        }

        /// <summary>
        /// Gets a collection using generic <see cref="BsonDocument"/>. Creates the collection if it does not exist.
        /// </summary>
        /// <param name="name">The collection name (case insensitive).</param>
        /// <param name="autoId">The auto-ID strategy to use when a document has no <c>_id</c> field. Default is <see cref="BsonAutoId.ObjectId"/>.</param>
        /// <returns>An <see cref="ILiteCollection{BsonDocument}"/> instance for the specified collection.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/> or whitespace.</exception>
        public ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return new LiteCollection<BsonDocument>(name, autoId, _engine, _mapper);
        }

        #endregion

        #region Transaction

        /// <summary>
        /// Begins a new transaction on the current thread. Transactions are created per-thread; only one transaction can exist per thread.
        /// </summary>
        /// <returns><see langword="true"/> if a new transaction was created; <see langword="false"/> if the current thread already has an active transaction.</returns>
        public bool BeginTrans() => _engine.BeginTrans();

        /// <summary>
        /// Commits the current transaction, persisting all changes to the database.
        /// </summary>
        /// <returns><see langword="true"/> if the transaction was committed; <see langword="false"/> if no active transaction exists.</returns>
        public bool Commit() => _engine.Commit();

        /// <summary>
        /// Rolls back the current transaction, discarding all uncommitted changes.
        /// </summary>
        /// <returns><see langword="true"/> if the transaction was rolled back; <see langword="false"/> if no active transaction exists.</returns>
        public bool Rollback() => _engine.Rollback();

        #endregion

        #region FileStorage

        private ILiteStorage<string> _fs = null;

        /// <summary>
        /// Gets a special collection for storing files/streams inside the database using default collections <c>_files</c> and <c>_chunks</c>.
        /// <para>Use <see cref="GetStorage{TFileId}"/> for custom options.</para>
        /// </summary>
        public ILiteStorage<string> FileStorage
        {
            get { return _fs ?? (_fs = this.GetStorage<string>()); }
        }

        /// <summary>
        /// Gets a new instance of <see cref="ILiteStorage{TFileId}"/> using a custom file ID type and custom collection names.
        /// <para>LiteDB supports multiple file storages using different collection names.</para>
        /// </summary>
        /// <typeparam name="TFileId">The type to use for file identifiers.</typeparam>
        /// <param name="filesCollection">The collection name for file metadata. Default is <c>_files</c>.</param>
        /// <param name="chunksCollection">The collection name for file chunks. Default is <c>_chunks</c>.</param>
        /// <returns>An <see cref="ILiteStorage{TFileId}"/> instance.</returns>
        public ILiteStorage<TFileId> GetStorage<TFileId>(string filesCollection = "_files", string chunksCollection = "_chunks")
        {
            return new LiteStorage<TFileId>(this, filesCollection, chunksCollection);
        }

        #endregion

        #region Shortcut

        /// <summary>
        /// Gets all user collection names in this database (does not include system collections).
        /// </summary>
        /// <returns>An enumerable of collection names.</returns>
        public IEnumerable<string> GetCollectionNames()
        {
            // use $cols system collection with type = user only
            var cols = this.GetCollection("$cols")
                .Query()
                .Where("type = 'user'")
                .ToDocuments()
                .Select(x => x["name"].AsString)
                .ToArray();

            return cols;
        }

        /// <summary>
        /// Checks if a collection exists in the database (collection names are case insensitive).
        /// </summary>
        /// <param name="name">The collection name to check.</param>
        /// <returns><see langword="true"/> if the collection exists; otherwise, <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/> or whitespace.</exception>
        public bool CollectionExists(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return this.GetCollectionNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Drops a collection and all its data and indexes.
        /// </summary>
        /// <param name="name">The collection name to drop.</param>
        /// <returns><see langword="true"/> if the collection was dropped; <see langword="false"/> if the collection does not exist.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/> or whitespace.</exception>
        public bool DropCollection(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return _engine.DropCollection(name);
        }

        /// <summary>
        /// Renames a collection.
        /// </summary>
        /// <param name="oldName">The current collection name.</param>
        /// <param name="newName">The new collection name.</param>
        /// <returns><see langword="true"/> if the collection was renamed; <see langword="false"/> if <paramref name="oldName"/> does not exist or <paramref name="newName"/> already exists.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="oldName"/> or <paramref name="newName"/> is <see langword="null"/> or whitespace.</exception>
        public bool RenameCollection(string oldName, string newName)
        {
            if (oldName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(oldName));
            if (newName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(newName));

            return _engine.RenameCollection(oldName, newName);
        }

        #endregion

        #region Execute SQL

        /// <summary>
        /// Executes SQL commands from a <see cref="TextReader"/> and returns a data reader.
        /// </summary>
        /// <param name="commandReader">The text reader containing SQL commands.</param>
        /// <param name="parameters">Optional parameters for the SQL command.</param>
        /// <returns>An <see cref="IBsonDataReader"/> containing the query results.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="commandReader"/> is <see langword="null"/>.</exception>
        public IBsonDataReader Execute(TextReader commandReader, BsonDocument parameters = null)
        {
            if (commandReader == null) throw new ArgumentNullException(nameof(commandReader));

            var tokenizer = new Tokenizer(commandReader);
            var sql = new SqlParser(_engine, tokenizer, parameters);
            var reader = sql.Execute();

            return reader;
        }

        /// <summary>
        /// Executes a SQL command string and returns a data reader.
        /// </summary>
        /// <param name="command">The SQL command string to execute.</param>
        /// <param name="parameters">Optional parameters for the SQL command.</param>
        /// <returns>An <see cref="IBsonDataReader"/> containing the query results.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is <see langword="null"/>.</exception>
        public IBsonDataReader Execute(string command, BsonDocument parameters = null)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            var tokenizer = new Tokenizer(command);
            var sql = new SqlParser(_engine, tokenizer, parameters);
            var reader = sql.Execute();

            return reader;
        }

        /// <summary>
        /// Executes a SQL command string with positional parameters and returns a data reader.
        /// </summary>
        /// <param name="command">The SQL command string to execute.</param>
        /// <param name="args">Positional parameters referenced as @0, @1, @2, etc. in the SQL command.</param>
        /// <returns>An <see cref="IBsonDataReader"/> containing the query results.</returns>
        public IBsonDataReader Execute(string command, params BsonValue[] args)
        {
            var p = new BsonDocument();
            var index = 0;

            foreach (var arg in args)
            {
                p[index.ToString()] = arg;
                index++;
            }

            return this.Execute(command, p);
        }

        #endregion

        #region Checkpoint/Rebuild

        /// <summary>
        /// Performs a database checkpoint, copying all committed transactions from the log file to the data file.
        /// </summary>
        public void Checkpoint()
        {
            _engine.Checkpoint();
        }

        /// <summary>
        /// Rebuilds the entire database to remove unused pages and reduce the data file size.
        /// </summary>
        /// <param name="options">Optional rebuild options. If <see langword="null"/>, uses default options.</param>
        /// <returns>The number of bytes saved by the rebuild operation.</returns>
        public long Rebuild(RebuildOptions options = null)
        {
            return _engine.Rebuild(options ?? new RebuildOptions());
        }

        #endregion

        #region Pragmas

        /// <summary>
        /// Gets the value of an internal engine variable (pragma).
        /// </summary>
        /// <param name="name">The pragma name.</param>
        /// <returns>The current value of the pragma.</returns>
        public BsonValue Pragma(string name)
        {
            return _engine.Pragma(name);
        }

        /// <summary>
        /// Sets the value of an internal engine variable (pragma).
        /// </summary>
        /// <param name="name">The pragma name.</param>
        /// <param name="value">The new value for the pragma.</param>
        /// <returns>The previous value of the pragma.</returns>
        public BsonValue Pragma(string name, BsonValue value)
        {
            return _engine.Pragma(name, value);
        }

        /// <summary>
        /// Gets or sets the database user version. Use this version number to track schema changes or migrations.
        /// </summary>
        public int UserVersion
        {
            get => _engine.Pragma(Pragmas.USER_VERSION);
            set => _engine.Pragma(Pragmas.USER_VERSION, value);
        }

        /// <summary>
        /// Gets or sets the database timeout used when waiting for locks during transactions.
        /// </summary>
        public TimeSpan Timeout
        {
            get => TimeSpan.FromSeconds(_engine.Pragma(Pragmas.TIMEOUT).AsInt32);
            set => _engine.Pragma(Pragmas.TIMEOUT, (int)value.TotalSeconds);
        }

        /// <summary>
        /// Gets or sets whether the database deserializes dates in UTC timezone or local timezone (default is local timezone).
        /// </summary>
        public bool UtcDate
        {
            get => _engine.Pragma(Pragmas.UTC_DATE);
            set => _engine.Pragma(Pragmas.UTC_DATE, value);
        }

        /// <summary>
        /// Gets or sets the database size limit in bytes. The new value must be equal to or larger than the current database size.
        /// </summary>
        public long LimitSize
        {
            get => _engine.Pragma(Pragmas.LIMIT_SIZE);
            set => _engine.Pragma(Pragmas.LIMIT_SIZE, value);
        }

        /// <summary>
        /// Gets or sets the auto-checkpoint threshold in pages (8 KB per page). 
        /// When the log file reaches this size, an automatic checkpoint occurs. 
        /// Set to 0 for manual-only checkpoints (no checkpoint on dispose). Default is 1000 pages.
        /// </summary>
        public int CheckpointSize
        {
            get => _engine.Pragma(Pragmas.CHECKPOINT);
            set => _engine.Pragma(Pragmas.CHECKPOINT, value);
        }

        /// <summary>
        /// Gets the database collation.
        /// <para>This option can only be changed during a rebuild process.</para>
        /// </summary>
        public Collation Collation
        {
            get => new Collation(_engine.Pragma(Pragmas.COLLATION).AsString);
        }

        #endregion

        /// <summary>
        /// Releases all resources used by the database. Commits any pending transactions and closes all file handles.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer for the <see cref="LiteDatabase"/> class.
        /// </summary>
        ~LiteDatabase()
        {
            this.Dispose(false);
        }

        /// <summary>
        /// Releases resources used by the database.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _disposeOnClose)
            {
                if (_checkpointOverride.HasValue)
                {
                    _engine.Pragma(Pragmas.CHECKPOINT, _checkpointOverride.Value);
                }

                _engine.Dispose();
            }
        }
    }
}
