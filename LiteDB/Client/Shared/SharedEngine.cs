using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Vector;
using LiteDB.Utils;

namespace LiteDB
{
    public partial class SharedEngine : ILiteEngine
    {
        // An operation's engine closes without checkpoint until the WAL reaches this many
        // pages: replaying up to 400 KiB at the next open costs less than the checkpoint's syncs.
        internal const int CLOSE_CHECKPOINT_PAGES = 50;

        private readonly EngineSettings _settings;
        private readonly NativeAdmission _admission;
        private readonly Mutex _mutex;
        private readonly SharedMutexTurnstile _turnstile;
        private readonly SharedMutexOwner _owner;
        // Guards the engine's user count, which a reader disposed on another thread also updates.
        private readonly object _useLock = new object();
        private readonly ISharedReaderRegistry _readers;
        private readonly SharedFileHandles _handles;
        private LiteEngine _engine;
        private WalRecoveryReport _recoveryReport;
        private volatile bool _transactionRunning = false;
        private Thread _transactionThread;
        private int _databaseUsers;
        private SharedMutexPin _transactionUse;
        private SharedLocalTransactionOwner _localTransactions;
        private SharedLocalTransactionOwner.Lease _localTransaction;
        // ABI-2 mapped WRITE tenure is held while this connection's writable core
        // exists.  The local named mutex still orders facades; the mapped byte orders
        // processes and provides a kernel-visible dead-man switch.
        private bool _mappedWriterHeld;
        // Read-only snapshots streaming under the mutex (no lease could be registered).
        private readonly HashSet<LiteEngine> _mutexSnapshots = new HashSet<LiteEngine>();
        private int _disposed;

        public SharedEngine(EngineSettings settings)
        {
            _settings = settings.Clone();
            _settings.TransactionOwner = new TransactionOwnerToken();
            _settings.SharedMode = true;
            ValidateOneFileSettings(_settings);
            if (NativeAdmission.IsFile(_settings)) NativeAdmission.Normalize(_settings);
            // Bind reopens, mutex and snapshot registry to one construction-time path.
            NativeAdmission.NormalizeSharedNamespace(_settings);
            _settings.SharedDurability = new SharedDurabilityState();
            _readers = _settings.SharedOneFile ? null : CreateReaderRegistry(_settings);
            // A rebuild would replace the files under live snapshot readers. Scan the
            // registry only when an open is about to rebuild, not on every operation.
            _settings.AutoRebuildAllowed = () => !_readers.OldestVersion().HasValue;
            // Each operation opens and closes an engine. Share one back-off so a
            // long-lived reader cannot make every close pay for partial checkpoint.
            _settings.CheckpointBackoff = new CheckpointBackoff();
            _settings.CloseCheckpointPages = CLOSE_CHECKPOINT_PAGES;
            // Opening the files is most of an operation's fixed cost. Keep the handles,
            // never the engine state: every operation still reads the files afresh.
            if (_settings.Filename != ":memory:" && _settings.Filename != ":temp:" &&
                _settings.DataStream == null && _settings.LogStream == null &&
                SharedFileHandles.IsSupportedFor(_settings.Filename))
                _settings.SharedFileHandles = _handles = new SharedFileHandles();

            var name = _mutexName = SharedMutexNameFactory.Create(_settings.Filename, _settings.SharedMutexNameStrategy);

            try
            {
                _mutex = SharedMutexFactory.Create(name);
                _turnstile = new SharedMutexTurnstile(SharedMutexFactory.Create(name + ".Turn"));
            }
            catch (NotSupportedException ex)
            {
                if (ex is PlatformNotSupportedException)
                {
                    throw;
                }

                throw new PlatformNotSupportedException("Shared mode is not supported in platforms that do not implement named mutex.", ex);
            }
            _owner = new SharedMutexOwner(_mutex, _turnstile, this.OnOwnerExited);
            try
            {
                if (NativeAdmission.IsFile(_settings)) _settings.SharedAdmission = _admission = NativeAdmission.Open(_settings, shared: true);
                _readers = _readers ?? CreateReaderRegistry(_settings);
                _settings.SharedReaderVersions = _readers.LiveVersions;
                BindParticipantSettings(_settings, _readers);
#if NET8_0_OR_GREATER
                if (_readers is SharedMemoryReaders mapped)
                {
                    _readCache = mapped.Table.ReadCache;
                    _localTransactions = mapped.Table.LocalTransactions;
                }
#endif
            }
            catch
            {
                _readers?.Dispose();
                _admission?.Dispose();
                _handles?.Dispose();
                _mutex.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Open database in safe mode. Returns the pin the operation runs under, or
        /// null when the operation owns a recursion of the named mutex instead.
        /// Open for an operation. A <paramref name="scoped"/> caller closes on the same thread
        /// before it returns; its ownership then takes the OS mutex directly on this thread.
        /// </summary>
        private SharedMutexPin OpenDatabase(bool scoped = false, bool writing = false)
        {
            this.ThrowIfForeignLocalTransaction();
            // Writers retire idle read handles before _useLock, preserving lock order.
            if (writing) this.RetireCoordinatedReads();
            if (writing && _mappedWriterHeld) this.EnterMappedWriter();
            var pin = _pin;
            if (pin != null)
            {
                if (pin.TryEnter()) return pin;
                // Another thread needs the mutex: end the pin at its next idle moment.
                if (!ReferenceEquals(pin.Owner, Thread.CurrentThread)) pin.RequestRelease(force: false);
            }

            // Acquire mutex for every call to open DB.
            var recoveredAbandonedOwner = this.EnterOwner(scoped && this.CanScope, writing);

            try
            {
                RejectAbandonedTransaction();
                if (writing) this.EnterMappedWriter(recoveredAbandonedOwner);
            }
            catch { this.ExitMappedWriter(false); this.EndWriterPressure(); _owner.Exit(); throw; }

            // Check, open and count under one lock. A reader disposed on another thread
            // closes the engine in CloseDatabase once the count reaches zero; between a
            // separate check and increment it could close the engine this call relies on.
            lock (_useLock)
            {
                try { this.AdmitLocked(); }
                catch { this.ExitMappedWriter(false); this.EndWriterPressure(); _owner.Exit(); throw; }
                // Don't create a new engine while a transaction is running.
                if (!_transactionRunning && _engine == null)
                {
                    try
                    {
                        this.OpenEngine(recoveredAbandonedOwner, writing: writing);
                    }
                    catch
                    {
                        this.ExitMappedWriter(false);
                        this.EndWriterPressure(); _owner.Exit();
                        throw;
                    }
                }
#if DEBUG || TESTING
                this.BeforeCountingUser?.Invoke();
#endif
                _databaseUsers++;
            }
            return null;
        }

        private LiteEngine CreateEngine(bool recoveredAbandonedOwner, EngineSettings settings = null)
        {
            _admission?.EnsureValid();
            const int retries = 100;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
#if DEBUG || TESTING
                    if (SimulateOpenEngine != null) return SimulateOpenEngine();
#endif
                    return new LiteEngine(settings ?? _settings);
                }
                catch (IOException ex) when (recoveredAbandonedOwner && IsWindowsLockViolation(ex) && attempt < retries)
                {
                    // On Windows an abandoned mutex can become available just before
                    // the dead process' file handles finish closing. Keep ownership
                    // while the transient sharing violation clears.
                    Thread.Sleep(20);
                }
            }
        }

        private static bool IsWindowsLockViolation(IOException exception)
        {
            const int ERROR_SHARING_VIOLATION = 32;
            const int ERROR_LOCK_VIOLATION = 33;
            var errorCode = exception.HResult & 0xFFFF;
            return errorCode == ERROR_SHARING_VIOLATION || errorCode == ERROR_LOCK_VIOLATION;
        }

        /// <summary>
        /// Dequeue stack and dispose database on empty stack. A pinned use ends an
        /// operation, or with <paramref name="hold"/> a reader or transaction.
        /// </summary>
        private void CloseDatabase(SharedMutexPin use = null, bool hold = false, int generation = -1)
        {
            if (use != null)
            {
                // The pin keeps the engine; its holder closes it.
                use.Exit(hold);
                return;
            }

            try
            {
                lock (_useLock)
                {
                    // A reader of an ownership that already ended (Dispose, exited
                    // owner) was counted by that ownership, which reset the count.
                    if (generation >= 0 && generation != _owner.Generation) return;
                    if (_databaseUsers > 0 && --_databaseUsers == 0 && !_transactionRunning && _engine != null)
                    {
                        var engine = _engine;
                        _engine = null;
                        try { this.CloseRetainedCore(engine); } finally { this.EndWriterPressure(); }
                    }
                }
            }
            finally
            {
                if (!_transactionRunning) _transactionThread = null;
                // Every OpenDatabase call acquires a recursion, even when it borrows.
                // Any thread may end it, for example when disposing a reader.
                _owner.Exit(generation);
            }
        }

        /// <summary>
        /// Runs on the mutex holder thread when the owner thread exited while owning
        /// the mutex. The mutex was never released meanwhile, but the owner's open
        /// reader or transaction can no longer complete: drop the engine without
        /// writing. An exited transaction owner is reported to the next caller.
        /// </summary>
        private void OnOwnerExited()
        {
            // The mutex stays owned until this cleanup returns; its closes reach caller streams.
            using (this.OwnershipFrame(HolderRetains))
            lock (_useLock)
            {
                _databaseUsers = 0;
                var engine = _engine;
                _engine = null;
                try
                {
                    if (engine != null) this.CloseRetainedCore(engine, checkpoint: false);
                    this.CloseMutexSnapshotsLocked();
                }
                // The mapped participant owns an explicit local lease that must be
                // terminated when its pin holder dies. Legacy Shared keeps the
                // transaction marker until the next opener reports the abandoned
                // owner, preserving the pre-mapped Shared contract.
                finally
                {
                    if (_localTransactions != null) this.EndTransaction(releaseHold: false);
                }
            }
            _handles?.CloseIdle();
        }

        #region Read Operation
        public BsonValue Pragma(string name)
        {
            return QueryDatabase(() => _engine.Pragma(name));
        }

        public bool Pragma(string name, BsonValue value)
        {
            return WriteDatabase(() => _engine.Pragma(name, value), scoped: true);
        }

        #endregion

        #region Write Operations

        public int Checkpoint() => WriteDatabase(() => _engine.Checkpoint(), scoped: true);

        public int Insert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return WriteDatabase(() => _engine.Insert(collection, docs, autoId), scoped: docs is BsonDocument[]);
        }

        public int Update(string collection, IEnumerable<BsonDocument> docs)
        {
            return WriteDatabase(() => _engine.Update(collection, docs), scoped: docs is BsonDocument[]);
        }

        public int UpdateMany(string collection, BsonExpression extend, BsonExpression predicate)
        {
            return WriteDatabase(() => _engine.UpdateMany(collection, extend, predicate), scoped: true);
        }

        public int Upsert(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            return WriteDatabase(() => _engine.Upsert(collection, docs, autoId), scoped: docs is BsonDocument[]);
        }

        public int Delete(string collection, IEnumerable<BsonValue> ids)
        {
            return WriteDatabase(() => _engine.Delete(collection, ids), scoped: ids is BsonValue[]);
        }

        public int DeleteMany(string collection, BsonExpression predicate)
        {
            return WriteDatabase(() => _engine.DeleteMany(collection, predicate), scoped: true);
        }

        public bool DropCollection(string name)
        {
            return WriteDatabase(() => _engine.DropCollection(name), scoped: true);
        }

        public bool RenameCollection(string name, string newName)
        {
            return WriteDatabase(() => _engine.RenameCollection(name, newName), scoped: true);
        }

        public bool DropIndex(string collection, string name)
        {
            return WriteDatabase(() => _engine.DropIndex(collection, name), scoped: true);
        }

        public bool EnsureIndex(string collection, string name, BsonExpression expression, bool unique)
        {
            return WriteDatabase(() => _engine.EnsureIndex(collection, name, expression, unique), scoped: true);
        }

        public bool EnsureVectorIndex(string collection, string name, BsonExpression expression, VectorIndexOptions options)
        {
            return WriteDatabase(() => _engine.EnsureVectorIndex(collection, name, expression, options), scoped: true);
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SharedEngine()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || Volatile.Read(ref _disposed) != 0) return;
            this.ThrowIfTeardownReentry();
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            this.RetireCoordinatedReads();
            // Any thread can end a pin; its holder closes the engine and releases. Read
            // under the lock that orders a starting pin's publication with this Dispose.
            SharedMutexPin pin;
            lock (_useLock) pin = _pin;
            this.MarkDisposeOverlaps(pin);
            if (pin != null)
            {
                pin.RequestRelease(force: true);
                if (!pin.CanWaitFrom(Thread.CurrentThread) || this.AdmittedDepth() != 0)
                {
                    // The pin's holder still closes its engine; its streams close on return.
                    this.DeferNativeRelease();
                    return;
                }
                try { pin.WaitReleased(); }
                catch
                {
                    this.WaitForAdmittedCalls();
                    this.DisposeOwnedResources();
                    throw;
                }
            }

            if (this.AdmittedDepth() != 0)
            {
                // Dispose from an ordinary operation or retained Read cannot close
                // the core under its own callback. The outer call drains it.
                Volatile.Write(ref _disposeAfterCall, Thread.CurrentThread);
                return;
            }
            // Calls admitted before Dispose started finish first; later ones are refused.
            this.WaitForAdmittedCalls();
            this.DisposeOwnedResources();
        }
    }
}
