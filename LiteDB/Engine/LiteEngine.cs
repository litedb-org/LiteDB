using LiteDB.Utils;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// A public class that take care of all engine data structure access - it´s basic implementation of a NoSql database
    /// Its isolated from complete solution - works on low level only (no linq, no poco... just BSON objects)
    /// [ThreadSafe]
    /// </summary>
    public partial class LiteEngine : ILiteEngine
    {
        #region Services instances

        private LockService _locker;

        private DiskService _disk;

        private WalIndexService _walIndex;

        private HeaderPage _header;

        private TransactionMonitor _monitor;

        private SortDisk _sortDisk;

        private EngineState _state;

        // immutable settings
        private readonly EngineSettings _settings;

        /// <summary>
        /// All system read-only collections for get metadata database information
        /// </summary>
        private Dictionary<string, SystemCollection> _systemCollections;

        /// <summary>
        /// Sequence cache for collections last ID (for int/long numbers only)
        /// </summary>
        private ConcurrentDictionary<string, long> _sequences;

        #endregion

        #region Ctor

        /// <summary>
        /// Initialize LiteEngine using connection memory database
        /// </summary>
        public LiteEngine()
            : this(new EngineSettings { DataStream = new MemoryStream() })
        {
        }

        /// <summary>
        /// Initialize LiteEngine using connection string using key=value; parser
        /// </summary>
        public LiteEngine(string filename)
            : this (new EngineSettings { Filename = filename })
        {
        }

        /// <summary>
        /// Initialize LiteEngine using initial engine settings
        /// </summary>
        public LiteEngine(EngineSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            this.Open();
        }

        #endregion

        #region Open & Close

        internal bool Open()
        {
            LOG($"start initializing{(_settings.ReadOnly ? " (readonly)" : "")}", "ENGINE");

            _systemCollections = new Dictionary<string, SystemCollection>(StringComparer.OrdinalIgnoreCase);
            _sequences = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // initialize engine state 
                _state = new EngineState(this, _settings);

                // A failed rebuild may have left stale data or no canonical file.
                // Check before upgrade, recovery, or DiskService can create a new file.
                RebuildRecovery.EnsureAvailable(_settings);

                // before initilize, try if must be upgrade
                if (_settings.Upgrade) this.TryUpgrade();

                // initialize disk service (will create database if needed)
                _disk = new DiskService(_settings, _state, MEMORY_SEGMENT_SIZES);

                // read page with no cache ref (has a own PageBuffer) - do not Release() support.
                // An existing file's header was just read and validated by the disk service.
                var buffer = _disk.TakeOpeningHeader() ?? _disk.ReadFull(FileOrigin.Data).First();

                // if first byte are 1 this datafile are encrypted but has do defined password to open
                if (buffer[0] == 1) throw new LiteException(LiteException.INVALID_PASSWORD, "This data file is encrypted and needs a password to open");

                // read header database page
                _header = new HeaderPage(buffer);
                _disk.FileVersion = _header.FileVersion;

                // if database is set to invalid state, need rebuild
                this.InvalidDatafileState = buffer[HeaderPage.P_INVALID_DATAFILE_STATE] != 0;
                if (buffer[HeaderPage.P_INVALID_DATAFILE_STATE] != 0 && _settings.AutoRebuild &&
                    (_settings.AutoRebuildAllowed?.Invoke() ?? true))
                {
                    // dispose disk access to rebuild process
                    _disk.Dispose();
                    _disk = null;

                    // rebuild database, create -backup file and include _rebuild_errors collection
                    this.Recovery(_header.Pragmas.Collation);

                    // re-initialize disk service
                    _disk = new DiskService(_settings, _state, MEMORY_SEGMENT_SIZES);

                    // read buffer header page again
                    buffer = _disk.TakeOpeningHeader() ?? _disk.ReadFull(FileOrigin.Data).First();

                    // if first byte are 1 this datafile are encrypted but has do defined password to open
                    if (buffer[0] == 1) throw new LiteException(LiteException.INVALID_PASSWORD, "This data file is encrypted and needs a password to open");

                    // read header database page
                    _header = new HeaderPage(buffer);
                    _disk.FileVersion = _header.FileVersion;
                }

                this.ValidateCollationStamp();

                // test for same collation
                if (_settings.Collation != null && _settings.Collation.ToString() != _header.Pragmas.Collation.ToString())
                {
                    throw new LiteException(0, $"Datafile collation '{_header.Pragmas.Collation}' is different from engine settings. Use Rebuild database to change collation.");
                }

                // initialize locker service
                _locker = new LockService(_header.Pragmas);

                // initialize wal-index service
                _walIndex = new WalIndexService(_disk, _locker, _settings.SharedReaderVersions, () => _header,
                    _settings.CheckpointBackoff, _settings.CoordinationSignals);

                // if exists log file, restore wal index references (can update full _header instance)
                if (_disk.GetFileLength(FileOrigin.Log) > 0 || _disk.ChecksumsEnabled)
                {
                    _walIndex.RestoreIndex(ref _header, this.ValidateCollationStamp);
                }

                this.ValidateCollationStamp();

                // initialize sort temp disk
                _sortDisk = new SortDisk(_settings.CreateTempFactory(), CONTAINER_SORT_SIZE, _header.Pragmas);

                // initialize transaction monitor as last service
                _monitor = new TransactionMonitor(_header, _locker, _disk, _walIndex, _settings.TransactionPageLimit);

                this.MigrateIndexOrdering();
                _disk.TrimTrailingPages();

                // register system collections
                this.InitializeSystemCollections();

                LOG("initialization completed", "ENGINE");

                return true;
            }
            catch (Exception ex)
            {
                LOG(ex.Message, "ERROR");

                this.Close(ex);
                throw;
            }
        }

        /// <summary>
        /// Normal close process:
        /// - Stop any new transaction
        /// - Stop operation loops over database (throw in SafePoint)
        /// - Wait for writer queue
        /// - Close disks
        /// - Clean variables
        /// Without <paramref name="checkpoint"/> nothing is written: a shared connection
        /// discards an engine whose view may predate another process' commits (#3005).
        /// A shared operation's close checkpoints only a WAL past its threshold; the
        /// connection's <paramref name="final"/> close always does (#3004).
        /// </summary>
        /// <summary>The opened header marks the data file invalid (a rebuild is due).</summary>
        internal bool InvalidDatafileState { get; private set; }

        internal List<Exception> Close(bool checkpoint = true, bool final = false)
        {
            if (_state.Disposed) return new List<Exception>();

            _state.Disposed = true;

            var tc = new TryCatch();

            // stop running all transactions
            tc.Catch(() => _monitor?.Dispose());

            if (checkpoint && !_settings.ReadOnly && _header?.Pragmas.Checkpoint > 0 && (final || this.CloseCheckpointDue()))
            {
                // Backfill safe pages; reclaim only when all readers have drained.
                tc.Catch(() => _walIndex?.TryCloseCheckpoint());
            }

            // close all disk streams (and delete log if empty)
            tc.Catch(() => _disk?.Dispose());

            // delete sort temp file
            tc.Catch(() => _sortDisk?.Dispose());

            // dispose lockers
            tc.Catch(() => _locker?.Dispose());

            return tc.Exceptions;
        }

        /// <summary>
        /// Every close checkpoints unless a shared connection set a threshold: its short-lived
        /// engines leave a smaller WAL to the next operation, whose replay costs less than the
        /// checkpoint's syncs. The WAL stays authoritative, so crash recovery is unchanged.
        /// </summary>
        private bool CloseCheckpointDue()
        {
            var threshold = _settings.CloseCheckpointPages;
            if (threshold <= 0) return true;
            var pages = Math.Min(threshold, _header.Pragmas.Checkpoint);
            return _disk.GetFileLength(FileOrigin.Log) >= (long)pages * PAGE_SIZE;
        }

        /// <summary>
        /// Exception close database:
        /// - Stop diskQueue
        /// - Stop any disk read/write (dispose)
        /// - Dispose sort disk
        /// - Dispose locker
        /// - Checks Exception type for INVALID_DATAFILE_STATE to auto rebuild on open
        /// </summary>
        internal List<Exception> Close(Exception ex, EngineState origin = null)
        {
            if (origin != null && !ReferenceEquals(origin, _state)) return new List<Exception>();
            if (_state.Disposed) return new List<Exception>();

            _state.Disposed = true;

            var tc = new TryCatch(ex);

            tc.Catch(() => _monitor?.Dispose());

            if (tc.InvalidDatafileState)
            {
                // Keep the data writer alive until the recovery marker is durable.
                tc.Catch(() => _disk?.MarkAsInvalidState());
            }

            // close disks streams
            tc.Catch(() => _disk?.Dispose());

            // close sort disk service
            tc.Catch(() => _sortDisk?.Dispose());

            // close engine lock service
            tc.Catch(() => _locker?.Dispose());

            return tc.Exceptions;
        }

        #endregion

#if DEBUG || TESTING
        // exposes for unit tests
        internal Action<long, FileOrigin> BeforePageRead { set => _state.BeforePageRead = value; }
        internal WalIndexService GetWalIndex() => _walIndex;
        internal Action<string> CheckpointStage { set => _state.CheckpointStage = value; }
        internal TransactionMonitor GetMonitor() => _monitor;
        internal Action<PageBuffer> SimulateDiskReadFail { set => _state.SimulateDiskReadFail = value; }
        internal Action<PageBuffer> SimulateDiskWriteFail { set => _state.SimulateDiskWriteFail = value; }
        internal Action SimulateBeforeTransactionAdmission { set => _locker.BeforeTransactionAdmission = value; }
        internal Action SimulateBeforeExclusiveAdmission { set => _locker.BeforeExclusiveAdmission = value; }
        internal Action SimulateAfterExclusiveAdmission { set => _locker.AfterExclusiveAdmission = value; }
#endif

        /// <summary>
        /// Run checkpoint command to copy log file into data file
        /// </summary>
        public int Checkpoint()
        {
            _state.Validate();
            try { return _settings.ReadOnly ? 0 : _walIndex.Checkpoint(); }
            catch (Exception ex)
            {
                _state.Handle(ex);
                throw;
            }
        }

        internal int ReadVersion => _walIndex.CurrentReadVersion;

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            this.Close();
        }
    }
}
