using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// All engine settings used to starts new engine
    /// </summary>
    public class EngineSettings
    {
        private int? _transactionPageLimit;

        /// <summary>
        /// Memory and transaction defaults for this database. Explicit limits
        /// take precedence regardless of property assignment order.
        /// </summary>
        public MemoryProfile MemoryProfile { get; set; } = MemoryProfile.Balanced;

        /// <summary>
        /// Get/Set custom stream to be used as datafile (can be MemoryStream or TempStream). Do not use FileStream - to use physical file, use "filename" attribute (and keep DataStream/WalStream null)
        /// </summary>
        public Stream DataStream { get; set; } = null;

        /// <summary>
        /// Get/Set custom stream to be used as log file. If is null, use a new TempStream (for TempStream datafile) or MemoryStream (for MemoryStream datafile)
        /// </summary>
        public Stream LogStream { get; set; } = null;

        /// <summary>
        /// Get/Set custom stream to be used as temp file. If is null, will create new FileStreamFactory with "-tmp" on name
        /// </summary>
        public Stream TempStream { get; set; } = null;

        /// <summary>
        /// Full path or relative path from DLL directory. Can use ':temp:' for temp database or ':memory:' for in-memory database. (default: null)
        /// </summary>
        public string Filename { get; set; }

        /// <summary>
        /// Get database password to decrypt pages
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        /// If database is new, initialize with allocated space (in bytes) (default: 0)
        /// </summary>
        public long InitialSize { get; set; } = 0;

        /// <summary>
        /// Soft page-cache target in bytes. Zero selects the storage-specific
        /// default of the selected <see cref="MemoryProfile"/>.
        /// </summary>
        public long CacheSize { get; set; } = 0;

        /// <summary>
        /// Pages retained by one transaction before a cooperative safepoint.
        /// Defaults to the profile threshold; explicit values must be positive.
        /// </summary>
        public int TransactionPageLimit
        {
            get => _transactionPageLimit ?? MemoryProfileDefaults.GetTransactionPageLimit(this.MemoryProfile);
            set => _transactionPageLimit = value;
        }

        /// <summary>
        /// Create database with custom string collection (used only to create database) (default: Collation.Default)
        /// </summary>
        public Collation Collation { get; set; }

        /// <summary>
        /// Indicate that engine will open files in readonly mode (and will not support any database change)
        /// </summary>
        public bool ReadOnly { get; set; } = false;

        /// <summary>
        /// After a Close with exception do a database rebuild on next open
        /// </summary>
        public bool AutoRebuild { get; set; } = false;

        /// <summary>
        /// Rebuild format v7 files before opening, retaining a backup. Writable v8/v9 opens automatically enable checksums.
        /// </summary>
        public bool Upgrade { get; set; } = false;

        /// <summary>
        /// Reject a document before it is written (insert, update, upsert, bulk) when it contains, at any depth
        /// including <c>_id</c>, a Local or Unspecified <see cref="DateTime"/> that does not exist in
        /// <see cref="TimeZoneInfo.Local"/> (the skipped hour of a daylight-saving transition). Such a value is
        /// otherwise stored as the following valid hour, which can surface as a duplicate key. Throws
        /// <see cref="ArgumentException"/>; inside an explicit transaction the failed operation rolls it back.
        /// The result depends on the time zone of the machine: it never fires on a UTC host, and in zones that
        /// switch at midnight a date-only value can be rejected. Utc, ambiguous, MinValue and MaxValue values are
        /// always accepted; queries are never checked. Prefer storing UTC values. (default: false)
        /// </summary>
        public bool RejectInvalidLocalTime { get; set; } = false;

        /// <summary>
        /// When true, each committed transaction is synced to the storage device (<c>FileStream.Flush(true)</c> on
        /// the log file) before Commit returns, so an acknowledged commit survives power loss and an operating
        /// system crash. This costs about one device sync per commit; transactions that batch many writes and
        /// InsertBulk pay it once. When false (the behaviour before 6.0), committed data is handed to the operating
        /// system only: it survives a crash of the process, but a power loss or operating system crash can lose the
        /// most recent commits. Checksummed WAL recovery discards incomplete transactions and their dependent tail.
        /// Checkpoints and file creation are synced either way.
        /// Not stored in the data file: the same file can be opened with either value. Has no effect on
        /// <c>:memory:</c>, <c>:temp:</c> and non-file streams, which cannot be synced. (default: true)
        /// </summary>
        public bool DurableCommits { get; set; } = true;

        /// <summary>
        /// Zone used by <see cref="RejectInvalidLocalTime"/>; null means <see cref="TimeZoneInfo.Local"/>.
        /// Internal so tests do not depend on the time zone of the machine.
        /// </summary>
        internal TimeZoneInfo LocalTimeZone { get; set; }

        /// <summary>
        /// Is used to transform a <see cref="BsonValue"/> from the database on read. This can be used to upgrade data from older versions.
        /// </summary>
        public Func<string, BsonValue, BsonValue> ReadTransform { get; set; }
        
        /// <summary>
        /// Determines how the mutex name is generated.
        /// </summary>
        public SharedMutexNameStrategy SharedMutexNameStrategy { get; set; }

        /// <summary>
        /// Create new IStreamFactory for datafile
        /// </summary>
        internal IStreamFactory CreateDataFactory(bool useAesStream = true)
        {
            if (this.DataStream != null)
            {
                return new StreamFactory(this.DataStream, useAesStream ? this.Password : null, false);
            }
            else if (this.Filename == ":memory:")
            {
                return new StreamFactory(new MemoryStream(), this.Password, true);
            }
            else if (this.Filename == ":temp:")
            {
                return new StreamFactory(new TempStream(), this.Password, true);
            }
            else if (!string.IsNullOrEmpty(this.Filename))
            {
                return new FileStreamFactory(this.Filename, this.Password, this.ReadOnly, false, useAesStream);
            }

            throw new ArgumentException("EngineSettings must have Filename or DataStream as data source");
        }

        internal long GetCacheSize()
        {
            var defaultSize = MemoryProfileDefaults.GetCacheSize(this.MemoryProfile,
                this.Filename == ":memory:" || this.DataStream is MemoryStream);
            if (this.CacheSize < 0) throw new ArgumentOutOfRangeException(nameof(this.CacheSize));
            if (this.CacheSize > 0) return this.CacheSize;
            return defaultSize;
        }

        /// <summary>
        /// Create new IStreamFactory for logfile
        /// </summary>
        internal IStreamFactory CreateLogFactory()
        {
            if (this.LogStream != null)
            {
                return new StreamFactory(this.LogStream, this.Password, false, isLog: true);
            }
            else if (this.Filename == ":memory:")
            {
                return new StreamFactory(new MemoryStream(), this.Password, true, isLog: true);
            }
            else if (this.Filename == ":temp:")
            {
                return new StreamFactory(new TempStream(), this.Password, true, isLog: true);
            }
            else if (!string.IsNullOrEmpty(this.Filename))
            {
                var logName = FileHelper.GetLogFile(this.Filename);

                return new FileStreamFactory(logName, this.Password, this.ReadOnly, false, isLog: true);
            }

            return new StreamFactory(new MemoryStream(), this.Password, true, isLog: true);
        }

        /// <summary>
        /// Create new IStreamFactory for temporary file (sort)
        /// </summary>
        internal IStreamFactory CreateTempFactory()
        {
            if (this.TempStream != null)
            {
                return new StreamFactory(this.TempStream, this.Password, false);
            }
            else if (this.Filename == ":memory:")
            {
                return new StreamFactory(new MemoryStream(), this.Password, true);
            }
            else if (this.Filename == ":temp:")
            {
                return new StreamFactory(new TempStream(), this.Password, true);
            }
            else if (!string.IsNullOrEmpty(this.Filename))
            {
                var tempName = FileHelper.GetTempFile(this.Filename);

                return new FileStreamFactory(tempName, this.Password, false, true);
            }

            return new StreamFactory(new TempStream(), this.Password, true);
        }
    }
}
