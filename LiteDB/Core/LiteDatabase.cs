﻿using System;
using System.IO;

namespace LiteDB
{
    /// <summary>
    /// The LiteDB database. Used for create a LiteDB instance and use all storage resoures. It's the database connection
    /// </summary>
    public partial class LiteDatabase : IDisposable
    {
        private LazyLoad<DbEngine> _engine;

        private BsonMapper _mapper;

        private Logger _log = new Logger();

        public Logger Log { get { return _log; } }
		
		public BsonMapper Mapper { get { return _mapper; } }

#if NETFULL || NETCORE
        /// <summary>
        /// Starts LiteDB database using a connection string for filesystem database
        /// </summary>
        public LiteDatabase(string connectionString, BsonMapper mapper = null)
        {
            var conn = new ConnectionString(connectionString);

            _mapper = mapper ?? BsonMapper.Global;

#if NETFULL
            var encrypted = !StringExtensions.IsNullOrWhiteSpace(conn.GetValue<string>("password", null));

            _engine = new LazyLoad<DbEngine>(() => new DbEngine(encrypted ? new EncryptedDiskService(conn, _log) : new FileDiskService(conn, _log), _log));
#elif NETCORE
            _engine = new LazyLoad<DbEngine>(() => new DbEngine(new FileDiskService(conn, _log), _log));
#endif
        }
#endif
        /// <summary>
        /// Initialize database using any read/write Stream (like MemoryStream)
        /// </summary>
        public LiteDatabase(Stream stream, BsonMapper mapper = null)
        {
            _mapper = mapper ?? BsonMapper.Global;
            _engine = new LazyLoad<DbEngine>(() => new DbEngine(new StreamDiskService(stream), _log));
        }

        /// <summary>
        /// Starts LiteDB database using full parameters
        /// </summary>
        public LiteDatabase(IDiskService diskService, BsonMapper mapper = null)
        {
            _mapper = mapper ?? BsonMapper.Global;
            _engine = new LazyLoad<DbEngine>(() => new DbEngine(diskService, _log));
        }

        /// <summary>
        /// Get/Set database version
        /// </summary>
        public ushort DbVersion
        {
            get { return _engine.Value.ReadDbVersion(); }
            set { _engine.Value.WriteDbVersion(value); }
        }

        public void Dispose()
        {
            if (_engine.IsValueCreated) _engine.Value.Dispose();
        }
    }
}