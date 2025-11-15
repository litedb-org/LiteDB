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
    /// <inheritdoc cref="ILiteDatabase"/>
    public partial class LiteDatabase : ILiteDatabase
    {
        #region Properties

        private readonly ILiteEngine _engine;
        private readonly BsonMapper _mapper;
        private readonly bool _disposeOnClose;
        private readonly int? _checkpointOverride;

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public ILiteCollection<T> GetCollection<T>(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            return new LiteCollection<T>(name, autoId, _engine, _mapper);
        }

        /// <inheritdoc/>
        public ILiteCollection<T> GetCollection<T>()
        {
            return this.GetCollection<T>(null);
        }

        /// <inheritdoc/>
        public ILiteCollection<T> GetCollection<T>(BsonAutoId autoId)
        {
            return this.GetCollection<T>(null, autoId);
        }

        /// <inheritdoc/>
        public ILiteCollection<BsonDocument> GetCollection(string name, BsonAutoId autoId = BsonAutoId.ObjectId)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return new LiteCollection<BsonDocument>(name, autoId, _engine, _mapper);
        }

        #endregion

        #region Transaction

        /// <inheritdoc/>
        public bool BeginTrans() => _engine.BeginTrans();

        /// <inheritdoc/>
        public bool Commit() => _engine.Commit();

        /// <inheritdoc/>
        public bool Rollback() => _engine.Rollback();

        #endregion

        #region FileStorage

        private ILiteStorage<string> _fs = null;

        /// <inheritdoc/>
        public ILiteStorage<string> FileStorage
        {
            get { return _fs ?? (_fs = this.GetStorage<string>()); }
        }

        /// <inheritdoc/>
        public ILiteStorage<TFileId> GetStorage<TFileId>(string filesCollection = "_files", string chunksCollection = "_chunks")
        {
            return new LiteStorage<TFileId>(this, filesCollection, chunksCollection);
        }

        #endregion

        #region Shortcut

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public bool CollectionExists(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return this.GetCollectionNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc/>
        public bool DropCollection(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(name));

            return _engine.DropCollection(name);
        }

        /// <inheritdoc/>
        public bool RenameCollection(string oldName, string newName)
        {
            if (oldName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(oldName));
            if (newName.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(newName));

            return _engine.RenameCollection(oldName, newName);
        }

        #endregion

        #region Execute SQL

        /// <inheritdoc/>
        public IBsonDataReader Execute(TextReader commandReader, BsonDocument parameters = null)
        {
            if (commandReader == null) throw new ArgumentNullException(nameof(commandReader));

            var tokenizer = new Tokenizer(commandReader);
            var sql = new SqlParser(_engine, tokenizer, parameters);
            var reader = sql.Execute();

            return reader;
        }

        /// <inheritdoc/>
        public IBsonDataReader Execute(string command, BsonDocument parameters = null)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            var tokenizer = new Tokenizer(command);
            var sql = new SqlParser(_engine, tokenizer, parameters);
            var reader = sql.Execute();

            return reader;
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void Checkpoint()
        {
            _engine.Checkpoint();
        }

        /// <inheritdoc/>
        public long Rebuild(RebuildOptions options = null)
        {
            return _engine.Rebuild(options ?? new RebuildOptions());
        }

        #endregion

        #region Pragmas

        /// <inheritdoc/>
        public BsonValue Pragma(string name)
        {
            return _engine.Pragma(name);
        }

        /// <inheritdoc/>
        public BsonValue Pragma(string name, BsonValue value)
        {
            return _engine.Pragma(name, value);
        }

        /// <inheritdoc/>
        public int UserVersion
        {
            get => _engine.Pragma(Pragmas.USER_VERSION);
            set => _engine.Pragma(Pragmas.USER_VERSION, value);
        }

        /// <inheritdoc/>
        public TimeSpan Timeout
        {
            get => TimeSpan.FromSeconds(_engine.Pragma(Pragmas.TIMEOUT).AsInt32);
            set => _engine.Pragma(Pragmas.TIMEOUT, (int)value.TotalSeconds);
        }

        /// <inheritdoc/>
        public bool UtcDate
        {
            get => _engine.Pragma(Pragmas.UTC_DATE);
            set => _engine.Pragma(Pragmas.UTC_DATE, value);
        }

        /// <inheritdoc/>
        public long LimitSize
        {
            get => _engine.Pragma(Pragmas.LIMIT_SIZE);
            set => _engine.Pragma(Pragmas.LIMIT_SIZE, value);
        }

        /// <inheritdoc/>
        public int CheckpointSize
        {
            get => _engine.Pragma(Pragmas.CHECKPOINT);
            set => _engine.Pragma(Pragmas.CHECKPOINT, value);
        }

        /// <inheritdoc/>
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
