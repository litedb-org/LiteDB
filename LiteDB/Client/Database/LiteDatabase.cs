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
    /// The LiteDB database. Used for create a LiteDB instance and use all storage resources. It's the database connection
    /// </summary>
    public partial class LiteDatabase : ILiteDatabase
    {
        #region Properties

        private readonly ILiteEngine _engine;
        private readonly LiteDatabaseContext _context;
        private readonly bool _disposeOnClose;
        private readonly int? _checkpointOverride;

        /// <summary>
        /// Get the BsonMapper used by this database instance and all objects it creates.
        /// </summary>
        public BsonMapper Mapper => _context.Mapper;

        internal LiteDatabaseContext Context => _context;

        #endregion

        #region Ctor

        /// <summary>
        /// Starts LiteDB database using a connection string for file system database
        /// </summary>
        public LiteDatabase(string connectionString, BsonMapper mapper = null)
            : this(new ConnectionString(connectionString), mapper)
        {
        }

        /// <summary>
        /// Starts LiteDB database using a connection string for file system database
        /// </summary>
        public LiteDatabase(ConnectionString connectionString, BsonMapper mapper = null)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));

            var resolvedMapper = ResolveMapper(mapper);
            _engine = connectionString.CreateEngine();
            _context = new LiteDatabaseContext(_engine, resolvedMapper);
            _disposeOnClose = true;
        }

        /// <summary>
        /// Starts LiteDB database using a generic Stream implementation (mostly MemoryStream).
        /// </summary>
        /// <param name="stream">DataStream reference </param>
        /// <param name="mapper">BsonMapper mapper reference</param>
        /// <param name="logStream">LogStream reference </param>
        public LiteDatabase(Stream stream, BsonMapper mapper = null, Stream logStream = null)
        {
            var settings = new EngineSettings
            {
                DataStream = stream ?? throw new ArgumentNullException(nameof(stream)),
                LogStream = logStream
            };

            var resolvedMapper = ResolveMapper(mapper);
            _engine = new LiteEngine(settings);
            _context = new LiteDatabaseContext(_engine, resolvedMapper);
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
        /// Start LiteDB database using a pre-exiting engine. When LiteDatabase instance dispose engine instance will be disposed too
        /// </summary>
        public LiteDatabase(ILiteEngine engine, BsonMapper mapper = null, bool disposeOnClose = true)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _context = new LiteDatabaseContext(_engine, ResolveMapper(mapper));
            _disposeOnClose = disposeOnClose;
        }

        private static BsonMapper ResolveMapper(BsonMapper mapper)
        {
            if (mapper != null) return mapper;

            var global = BsonMapper.Global ??
                throw new InvalidOperationException("BsonMapper.Global cannot be null when no mapper is supplied.");

            return global.Clone();
        }

        #endregion

        #region Collections

        private const string DocumentCollectionJustification =
            "LiteCollection<BsonDocument> never discovers model members: its constructor skips entity mapping for BsonDocument, " +
            "ToDocument returns a BsonDocument unchanged, and Deserialize returns the stored document for typeof(BsonDocument). " +
            "LINQ on it serializes captured values without model mapping and rejects application objects. The only lambdas that still " +
            "reach runtime mapping are object initializers and anonymous types, and for those the C# compiler's own Expression.Bind and " +
            "Expression.New calls already report IL2026 at the consumer's call site. " +
            "The document scenarios of LiteDB.AotSmokeTests run this path trimmed and as Native AOT.";

        /// <summary>
        /// Get a collection using an entity class as strong typed document. If collection does not exist, create a new one.
        /// </summary>
        /// <param name="name">Collection name (case insensitive)</param>
        /// <param name="autoId">Define autoId data type (when object contains no id field)</param>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        public ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            return new LiteCollection<T>(name, autoId, _context);
        }

        /// <summary>
        /// Gets a typed collection backed exclusively by a source-generated execution map.
        /// </summary>
        /// <typeparam name="T">The explicitly generated entity type.</typeparam>
        /// <param name="name">The required case-insensitive collection name.</param>
        /// <param name="autoId">The auto-ID type when the entity map has no auto-ID member.</param>
        /// <returns>The typed collection.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is missing.</exception>
        /// <exception cref="InvalidOperationException">Thrown when no generated mapper is registered or a registered generated execution map requires an unsupported mapper configuration.</exception>
        public ILiteCollection<T> GetGeneratedCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            if (Mapper.HasGeneratedEntityMapper(typeof(T)) == false)
            {
                throw new InvalidOperationException($"No source-generated entity mapper is registered for '{typeof(T).FullName}'.");
            }

            if (Mapper.TryGetGeneratedExecutionMap<T>(out var generatedMap) == false)
            {
                throw new InvalidOperationException($"No source-generated execution map is registered for '{typeof(T).FullName}'.");
            }

            Mapper.ValidateGeneratedExecutionConfiguration(generatedMap);
            return new GeneratedLiteCollection<T>(
                name,
                autoId,
                _context,
                Mapper.GetGeneratedEntityMapper(typeof(T)),
                generatedMap,
                () => Mapper.ValidateGeneratedExecutionConfiguration(generatedMap));
        }

        /// <summary>
        /// Get a collection using a name based on typeof(T).Name (BsonMapper.ResolveCollectionName function)
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        public ILiteCollection<T> GetCollection<T>()
        {
            return this.GetCollection<T>(null);
        }

        /// <summary>
        /// Get a collection using a name based on typeof(T).Name (BsonMapper.ResolveCollectionName function)
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeModelMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        public ILiteCollection<T> GetCollection<T>(BsonAutoId autoId)
        {
            return this.GetCollection<T>(null, autoId);
        }

        /// <summary>
        /// Get a collection using a generic BsonDocument. If collection does not exist, create a new one.
        /// </summary>
        /// <param name="name">Collection name (case insensitive)</param>
        /// <param name="autoId">Define autoId data type (when document contains no _id field)</param>
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = DocumentCollectionJustification)]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = DocumentCollectionJustification)]
        public ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return new LiteCollection<BsonDocument>(name, autoId, _context);
        }

        #endregion

        #region Transaction

        /// <summary>
        /// Initialize a new transaction. Transaction are created "per-thread". There is only one single transaction per thread.
        /// Return true when created; false joins the current thread transaction. Keep the block synchronous, with no await.
        /// </summary>
        public bool BeginTrans() => _engine.BeginTrans();

        /// <summary>
        /// Commit the current thread transaction; throws if only other threads have explicit transactions.
        /// </summary>
        public bool Commit() => _engine.Commit();

        /// <summary>
        /// Roll back the current thread transaction. Returns false when this thread has none, even while other threads have explicit transactions.
        /// </summary>
        public bool Rollback() => _engine.Rollback();

        #endregion

        #region FileStorage

        private ILiteStorage<string> _fs = null;

        /// <summary>
        /// Returns a special collection for storage files/stream inside datafile. Use _files and _chunks collection names. FileId is implemented as string. Use "GetStorage" for custom options
        /// </summary>
        public ILiteStorage<string> FileStorage
        {
            [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The ID type is string, which uses BSON-native conversion; LiteFileInfo<string> has a statically authored map. No application type is reflected.")]
            [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "String IDs and the statically authored LiteFileInfo<string> map require no runtime type construction.")]
            get { return _fs ??= this.GetStorage<string>(); }
        }

        /// <summary>
        /// Get new instance of Storage using custom FileId type, custom "_files" collection name and custom "_chunks" collection. LiteDB support multiples file storages (using different files/chunks collection names)
        /// </summary>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AotCompatibility.RuntimeFileIdMapping)]
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AotCompatibility.RuntimeTypeConstruction)]
        public ILiteStorage<TFileId> GetStorage<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(AotCompatibility.FileIdMembers)] TFileId>(string filesCollection = "_files", string chunksCollection = "_chunks")
        {
            return new LiteStorage<TFileId>(this, filesCollection, chunksCollection);
        }

        #endregion

        #region Shortcut

        /// <summary>
        /// Get all collections name inside this database.
        /// </summary>
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
        /// Checks if a collection exists on database. Collection name is case insensitive
        /// </summary>
        public bool CollectionExists(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return this.GetCollectionNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Drop a collection and all data + indexes
        /// </summary>
        public bool DropCollection(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return _engine.DropCollection(name);
        }

        /// <summary>
        /// Rename a collection. Returns false if oldName does not exists or newName already exists
        /// </summary>
        public bool RenameCollection(string oldName, string newName)
        {
            if (oldName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(oldName));
            if (newName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(newName));

            return _engine.RenameCollection(oldName, newName);
        }

        #endregion

        #region Execute SQL

        /// <summary>
        /// Execute SQL commands and return as data reader.
        /// </summary>
        public IBsonDataReader Execute(TextReader commandReader, BsonDocument parameters = null)
        {
            if (commandReader == null) throw new ArgumentNullException(nameof(commandReader));

            var tokenizer = new Tokenizer(commandReader);
            var sql = new SqlParser(_engine, tokenizer, parameters);
            var reader = sql.Execute();

            return reader;
        }

        /// <summary>
        /// Execute SQL commands and return as data reader
        /// </summary>
        public IBsonDataReader Execute(string command, BsonDocument parameters = null)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            return this.ExecuteSql(command, parameters);
        }

        /// <summary>
        /// Execute SQL commands and return as data reader
        /// </summary>
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
        /// Do database checkpoint. Copy all commited transaction from log file into datafile.
        /// </summary>
        public void Checkpoint()
        {
            _engine.Checkpoint();
        }

        /// <summary>
        /// Rebuild all database to remove unused pages - reduce data file
        /// Every omitted option (password, collation) keeps its current value; decrypting requires RebuildOptions.RemovePassword.
        /// </summary>
        public long Rebuild(RebuildOptions options = null)
        {
            return _engine.Rebuild(options);
        }

        #endregion

        #region Pragmas

        /// <summary>
        /// Get value from internal engine variables
        /// </summary>
        public BsonValue Pragma(string name)
        {
            return _engine.Pragma(name);
        }

        /// <summary>
        /// Set new value to internal engine variables
        /// </summary>
        public BsonValue Pragma(string name, BsonValue value)
        {
            return _engine.Pragma(name, value);
        }

        /// <summary>
        /// Get/Set database user version - use this version number to control database change model
        /// </summary>
        public int UserVersion
        {
            get => _engine.Pragma(Pragmas.USER_VERSION);
            set => _engine.Pragma(Pragmas.USER_VERSION, value);
        }

        /// <summary>
        /// Get/Set database timeout - this timeout is used to wait for unlock using transactions
        /// </summary>
        public TimeSpan Timeout
        {
            get => TimeSpan.FromSeconds(_engine.Pragma(Pragmas.TIMEOUT).AsInt32);
            set => _engine.Pragma(Pragmas.TIMEOUT, (int)value.TotalSeconds);
        }

        /// <summary>
        /// Get/Set if database will deserialize dates in UTC timezone or Local timezone (default: Local)
        /// </summary>
        public bool UtcDate
        {
            get => _engine.Pragma(Pragmas.UTC_DATE);
            set => _engine.Pragma(Pragmas.UTC_DATE, value);
        }

        /// <summary>
        /// Get/Set database limit size (in bytes). New value must be equals or larger than current database size
        /// </summary>
        public long LimitSize
        {
            get => _engine.Pragma(Pragmas.LIMIT_SIZE);
            set => _engine.Pragma(Pragmas.LIMIT_SIZE, value);
        }

        /// <summary>
        /// Get/Set in how many pages (8 Kb each page) log file will auto checkpoint (copy from log file to data file). Use 0 to manual-only checkpoint (and no checkpoint on dispose)
        /// Default: 1000 pages
        /// </summary>
        public int CheckpointSize
        {
            get => _engine.Pragma(Pragmas.CHECKPOINT);
            set => _engine.Pragma(Pragmas.CHECKPOINT, value);
        }

        /// <summary>
        /// Get database collection (this options can be changed only in rebuild proces)
        /// </summary>
        public Collation Collation
        {
            get => new Collation(_engine.Pragma(Pragmas.COLLATION).AsString);
        }

        #endregion

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~LiteDatabase()
        {
            this.Dispose(false);
        }

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
