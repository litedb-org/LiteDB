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

        // Volatile: a reopen publishes a new state while other threads call in (see EnsureOpen).
        private volatile EngineState _state;

        // The caller's settings, or the engine's own copy once it opened (or reopened) read-only on its own.
        private EngineSettings _settings;

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

            // An earlier operation of this shared connection hit a write or sync failure (decision 6):
            // its later operations open read-only until the connection is reopened. The engine's own copy
            // of the settings: the caller's stay as they were.
            var connectionFailure = settings.SharedDurability?.WriteFailure;
            if (connectionFailure != null && !_settings.ReadOnly)
            {
                _settings = _settings.Clone();
                _settings.ReadOnly = true;
                _settings.LegacyIndexScan = true;
                _settings.WriteFailure = connectionFailure;
                _settings.ReadOnlyCause = connectionFailure.ToString();
            }

            try
            {
                this.Open();
            }
            catch (IOException ex) when (!_settings.ReadOnly && DiskService.IsUnsyncedStorage(ex))
            {
                // The data file cannot sync (#2242) and this open had to convert, migrate or repair the
                // file first: it removed neither the log nor its header's recovery copy. Read the files
                // as they are instead of failing; writes throw and $database.readOnlyReason says why.
                // The engine's own copy of the settings: the caller's stay as they were.
                _settings = _settings.Clone();
                _settings.ReadOnly = true;
                _settings.LegacyIndexScan = true;
                _settings.ReadOnlyCause = ex.Message;
                this.Open();
            }
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
#if DEBUG || TESTING
                _settings.ReopenStage?.Invoke("state-published");
#endif

                // A failed rebuild may have left stale data or no canonical file.
                // Check before upgrade, recovery, or DiskService can create a new file.
                RebuildRecovery.EnsureAvailable(_settings);

                // before initilize, try if must be upgrade
                if (_settings.Upgrade) this.TryUpgrade();

                // initialize disk service (will create database if needed)
                _disk = _state.Disk = new DiskService(_settings, _state, MEMORY_SEGMENT_SIZES);
                this.LogUnverifiedSyncs();

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
                // A rebuild replaces files: a file this engine opened read-only on its own (after a write
                // failure, or because its data file cannot sync) is opened as it is, nothing may write there.
                if (buffer[HeaderPage.P_INVALID_DATAFILE_STATE] != 0 && _settings.AutoRebuild &&
                    _settings.WriteFailure == null && _settings.ReadOnlyCause == null)
                {
                    // Announce replacement before checking the external leases: a
                    // later admission must not pass a scan that permitted rebuilding.
                    using var structural = new StructuralScope(_settings.CoordinationSignals);
                    if (_settings.AutoRebuildAllowed?.Invoke() ?? true)
                    {
                        // dispose disk access to rebuild process
                        _disk.Dispose();
                        _disk = null;

                        // rebuild database, create -backup file and include _rebuild_errors collection
                        this.Recovery(_header.Pragmas.Collation);

                        // re-initialize disk service
                        _disk = _state.Disk = new DiskService(_settings, _state, MEMORY_SEGMENT_SIZES);

                        // read buffer header page again
                        buffer = _disk.TakeOpeningHeader() ?? _disk.ReadFull(FileOrigin.Data).First();

                        // if first byte are 1 this datafile are encrypted but has do defined password to open
                        if (buffer[0] == 1) throw new LiteException(LiteException.INVALID_PASSWORD, "This data file is encrypted and needs a password to open");

                        // read header database page
                        _header = new HeaderPage(buffer);
                        _disk.FileVersion = _header.FileVersion;
                    }
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

            // After a failure (its stop in progress, or recorded) nothing may be written, and no sync
            // retried on the handle that failed.
            if (checkpoint && !_settings.ReadOnly && !_state.Stopped && _state.WriteFailure == null &&
                _header?.Pragmas.Checkpoint > 0 && (final || this.CloseCheckpointDue()))
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
        /// Implementation note 3 of docs/decisions/durability-policy.md: without a C library on Unix the
        /// runtime's Flush(true) loses sync errors. Syncs are still attempted and commits acknowledged,
        /// with durableLogFlush false; say so once per open where durable commits were asked for.
        /// </summary>
        private void LogUnverifiedSyncs()
        {
            if (_settings.DurableCommits && !_settings.ReadOnly && _disk.LogSyncUnverified)
                LOG("durable commits requested, but no C library could be bound: file syncs go through the runtime's " +
                    "Flush(true), which cannot report a failed sync, so commits are not claimed durable (durableLogFlush=false)", "DISK");
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

            this.RememberLostTransactions(_state);
            tc.Catch(() => _monitor?.Dispose());

            // A read-only engine never writes: the mark would only reach a caller's writable stream.
            if (tc.InvalidDatafileState && !_settings.ReadOnly)
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
        internal Action<PageBuffer> SimulateDataWriteFail { set => _state.SimulateDataWriteFail = value; }
        internal bool SimulateDeferredCheckpointStop { set => _state.DeferCheckpointStop = value; }
        internal Action SimulateAfterFailedWalWrite { set => _state.AfterFailedWalWrite = value; }
        internal Action<string> SimulateBeforeSyncLock { set => _state.BeforeSyncLock = value; }
        internal EngineState GetState() => _state;
        internal DiskService GetDisk() => _disk;
        internal Action<string> SimulateCrashPoint { set => _state.AtCrashPoint = value; }
        internal Action SimulateBeforeTransactionAdmission { set => _locker.BeforeTransactionAdmission = value; }
        internal Action SimulateBeforeExclusiveAdmission { set => _locker.BeforeExclusiveAdmission = value; }
        internal Action SimulateAfterExclusiveAdmission { set => _locker.AfterExclusiveAdmission = value; }
#endif

        /// <summary>
        /// Run checkpoint command to copy log file into data file
        /// </summary>
        public int Checkpoint()
        {
            this.EnsureOpen();
            // Continuing read-only after a write failure: the caller's drain did not happen (decision 6).
            if (_settings.WriteFailure != null) throw this.ReadOnlyWrite();
            var state = _state;
            try { return _settings.ReadOnly ? 0 : _walIndex.Checkpoint(); }
            catch (Exception ex)
            {
                state.Handle(ex);
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
            this.CloseForDispose();
        }
    }
}
