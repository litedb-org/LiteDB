using LiteDB.Utils;
using LiteDB.Client.Direct;
using LiteDB.Client.Shared;

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

        // The caller's settings, or the engine's own copy once it opened read-only on its own.
        private EngineSettings _settings;

        private DirectProcessHost _host;
        private DirectFacadeLifetime _lifetime;
        private NativeAdmission _sharedAdmission;

        /// <summary>
        /// All system read-only collections for get metadata database information
        /// </summary>
        private Lazy<Dictionary<string, SystemCollection>> _systemCollections;

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
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _settings = settings;
            if (!settings.BypassDirectHost && !settings.SharedMode && NativeAdmission.IsFile(settings))
            {
                try
                {
                    _host = DirectProcessHost.Attach(settings.Clone());
                    _settings = _host.Settings;
                    try
                    {
                        this.CopyHostServices();
                        _lifetime = new DirectFacadeLifetime(_host, _monitor);
                    }
                    catch
                    {
                        _host.Release(_monitor);
                        _host = null;
                        throw;
                    }
                }
                catch (IOException error) when (settings.ReadOnly &&
                    (error is FileNotFoundException || error is DirectoryNotFoundException))
                {
                    throw new LiteException(LiteException.FILE_NOT_FOUND, error,
                        "File '{0}' does not exist and cannot be created in read-only mode.", settings.Filename);
                }
            }
            else
            {
                _sharedAdmission = settings.SharedAdmission?.Retain();
                this.OpenWithReadOnlyFallback(settings);
            }
        }

        private void OpenWithReadOnlyFallback(EngineSettings settings)
        {
            // A caller's stream that cannot be written is never repaired, migrated or checkpointed.
            if (!settings.ReadOnly && !settings.ReadOnlyStorage &&
                (settings.DataStream?.CanWrite == false || settings.LogStream?.CanWrite == false))
            {
                _settings = settings.Clone();
                _settings.ReadOnlyStorage = true;
            }

            try
            {
                this.Open();
            }
            catch (ReadOnlyOpenRequiredException)
            {
                // Keep retry flags on an engine-owned copy, including explicit ReadOnlyStorage settings.
                if (ReferenceEquals(_settings, settings)) _settings = settings.Clone();
                _settings.ReadOnly = true;
                _settings.LegacyIndexScan = true;
                this.Open();
            }
        }

        #endregion

        #region Open & Close

        internal bool Open()
        {
            LOG($"start initializing{(_settings.ReadOnly ? " (readonly)" : "")}", "ENGINE");

            _systemCollections = new Lazy<Dictionary<string, SystemCollection>>(this.CreateSystemCollections);
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
                _disk = _state.Disk = new DiskService(_settings, _state, MEMORY_SEGMENT_SIZES);

                // read page with no cache ref (has a own PageBuffer) - do not Release() support.
                // An existing file's header was just read and validated by the disk service.
                _header = _disk.TakeOpeningHeader() ?? new HeaderPage(_disk.ReadFull(FileOrigin.Data).First());
                var buffer = _header.Buffer;

                // if first byte are 1 this datafile are encrypted but has do defined password to open
                if (buffer[0] == 1) throw new LiteException(LiteException.INVALID_PASSWORD, "This data file is encrypted and needs a password to open");

                // read header database page
                _disk.FileVersion = _header.FileVersion;

                // if database is set to invalid state, need rebuild
                this.InvalidDatafileState = buffer[HeaderPage.P_INVALID_DATAFILE_STATE] != 0;
                // A rebuild replaces files: a caller's stream is opened as it is.
                if (buffer[HeaderPage.P_INVALID_DATAFILE_STATE] != 0 && _settings.AutoRebuild && _settings.DataStream == null)
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
                        _header = _disk.TakeOpeningHeader() ?? new HeaderPage(_disk.ReadFull(FileOrigin.Data).First());
                        buffer = _header.Buffer;

                        // if first byte are 1 this datafile are encrypted but has do defined password to open
                        if (buffer[0] == 1) throw new LiteException(LiteException.INVALID_PASSWORD, "This data file is encrypted and needs a password to open");

                        // read header database page
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
                // WAL recovery can replace the header; waits must use its current pragmas.
                _locker = new LockService(() => _header.Pragmas.Timeout);

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
                _monitor = new TransactionMonitor(_header, _locker, _disk, _walIndex, _settings.TransactionPageLimit, _settings.TransactionOwner);

                this.MigrateIndexOrdering();
                _disk.TrimTrailingPages();

                LOG("initialization completed", "ENGINE");

                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is ReadOnlyOpenRequiredException)) LOG(ex.Message, "ERROR");

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
                _header != null && this.CheckpointPages > 0 && (final || this.CloseCheckpointDue()))
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

            _sharedAdmission?.Dispose();
            return tc.Exceptions;
        }

        /// <summary>WAL size (in pages) that triggers a checkpoint; 0 disables automatic checkpoints.</summary>
        // Storage that cannot be written is never checkpointed: writes require a caller-supplied log.
        // Neither is a read-only engine (its read transactions complete through CommitAndReleaseTransaction).
        private int CheckpointPages => _settings.ReadOnlyStorage || _settings.ReadOnly ? 0 : _settings.CheckpointEachCommit ? 1 : _header.Pragmas.Checkpoint;

        /// <summary>
        /// Every close checkpoints unless a shared connection set a threshold: its short-lived
        /// engines leave a smaller WAL to the next operation, whose replay costs less than the
        /// checkpoint's syncs. The WAL stays authoritative, so crash recovery is unchanged.
        /// </summary>
        private bool CloseCheckpointDue()
        {
            var threshold = _settings.CloseCheckpointPages;
            if (threshold <= 0) return true;
            var pages = Math.Min(threshold, this.CheckpointPages);
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

            // Read-only opens and storage that cannot be written cannot take the mark:
            // marking would journal the header into a caller's writable log.
            if (tc.InvalidDatafileState && !_settings.ReadOnlyStorage && !_settings.ReadOnly)
            {
                Reachability.Sometimes("maintenance:invalid-datafile-state-close");
                // Keep the data writer alive until the recovery marker is durable.
                tc.Catch(() => _disk?.MarkAsInvalidState());
            }

            // close disks streams
            tc.Catch(() => _disk?.Dispose());

            // close sort disk service
            tc.Catch(() => _sortDisk?.Dispose());

            // close engine lock service
            tc.Catch(() => _locker?.Dispose());

            _sharedAdmission?.Dispose();
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
            if (_host != null)
            {
                _monitor.ValidateOwner();
                return _host.Maintain(() => _host.Engine.Checkpoint());
            }
            _state.Validate();
            // Fail before a checkpoint journals the header into a caller's log it cannot finish.
            if (_settings.ReadOnlyStorage && !_settings.ReadOnly && _disk.GetFileLength(FileOrigin.Log) > 0)
                throw new NotSupportedException("A stream of this database cannot be written, so its log cannot be checkpointed.");
            if (_settings.ReadOnlyStorage) return 0;
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
            if (_host == null) this.Close();
            else _lifetime?.Dispose();
        }
    }
}
