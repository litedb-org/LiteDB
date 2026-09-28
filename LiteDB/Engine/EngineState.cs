using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class EngineState
    {
        public volatile bool Disposed = false;
        // Set once a failure's teardown closed the engine's services (CompleteStop).
        private readonly ManualResetEventSlim _closed = new ManualResetEventSlim();
        private Exception _exception;
        private WriteFailure _writeFailure;

        /// <summary>A failure stopped this engine (<see cref="BeginStop"/>).</summary>
        internal bool Stopped => Volatile.Read(ref _exception) != null;

        /// <summary>
        /// The write or sync failure that stopped this engine (decision 6 of
        /// docs/decisions/durability-policy.md); null when none, or when the engine stopped otherwise.
        /// </summary>
        internal WriteFailure WriteFailure => Volatile.Read(ref _writeFailure);

        /// <summary>Threads whose explicit transaction the failure ended: their Commit must throw.</summary>
        internal int[] LostTransactionThreads { get; set; }

        /// <summary>
        /// This engine's disk service (set by LiteEngine.Open): a write or sync failure that reaches
        /// <see cref="Handle"/> or <see cref="StopAfter"/> is recorded through it. Null in unit tests.
        /// </summary>
        internal DiskService Disk { get; set; }

        /// <summary>
        /// The write failure this engine opened read-only after: its own engine's before the reopen, or
        /// an earlier engine's of the same shared connection. Null otherwise.
        /// </summary>
        internal WriteFailure ReopenedAfter => _settings?.WriteFailure;

        // A failure recorded where the engine could not stop (a $database read): the next call stops it.
        private volatile bool _stopDue;

        /// <summary>A recorded failure waits for its stop (<see cref="StopLater"/>).</summary>
        internal bool StopDue => _stopDue && !this.Stopped;

        private readonly LiteEngine _engine; // can be null for unit tests
        private readonly EngineSettings _settings;

#if DEBUG || TESTING
        public Action<long, FileOrigin> BeforePageRead;
        public Action<string> CheckpointStage;
        public Action<PageBuffer> SimulateDiskReadFail = null;
        public Action<PageBuffer> SimulateDiskWriteFail = null;

        // Test hook: a failed checkpoint stops the engine only after releasing its locks (the timing
        // before the stop moved inside the WAL writer), to reach defences behind that stop.
        public bool DeferCheckpointStop;

        // Test hook: runs after a failed WAL write released the writer, before the engine's teardown.
        public Action AfterFailedWalWrite;

        // Test hook: this engine's crash points (SimulateProcessCrash sees every engine's).
        public Action<string> AtCrashPoint;
        internal Action<PageBuffer> SimulateDataWriteFail;
        internal static Action<string> SimulateProcessCrash;
        internal static Action<long> ObserveSortSpill;
        internal static Action<PageBuffer> ObserveCacheEviction;
#endif

        public EngineState(LiteEngine engine, EngineSettings settings)
        {
            _engine = engine;
            _settings = settings;
#if DEBUG || TESTING
            CheckpointStage = settings?.CheckpointStage;
#endif
        }

        public void Validate()
        {
            var failure = Volatile.Read(ref _exception);
            if (failure != null) throw failure;
            if (this.Disposed) throw Volatile.Read(ref _exception) ?? LiteException.EngineDisposed();
        }

        /// <summary>
        /// An operation failed: false when the failure stopped the engine, true when the caller rolls its
        /// transaction back and the engine goes on. An I/O failure or a damaged file stops it; a write or
        /// sync failure is recorded first, so the engine reopens read-only on its next call (decision 6
        /// of docs/decisions/durability-policy.md), while a failed read or a damaged file keeps it
        /// closed (implementation note 6).
        /// </summary>
        public bool Handle(Exception ex)
        {
            LOG(ex.Message, "ERROR");

            // Refused because the data file cannot sync, before anything was written (#2242): not a
            // failure (implementation note 6). The transaction rolls back and the caller gets the
            // refusal, as at the WAL limit. A refusal its throw site recorded stops the engine below.
            if (DiskService.IsRefusedBeforeWrite(ex) && this.WriteFailure == null) return true;

            if (ex is IOException ||
                (ex is LiteException lex && (lex.ErrorCode == LiteException.INVALID_DATAFILE_STATE || lex.ErrorCode == LiteException.CHECKSUM_MISMATCH)))
            {
                this.StopAfter("A write", ex);

                return false;
            }

            return true;
        }

        /// <summary>
        /// Stop the engine after <paramref name="ex"/>. A write or sync failure (one that names its file,
        /// <see cref="WriteFailure.FileDataKey"/>) is recorded first as <paramref name="operation"/>, so the
        /// next call reopens the engine read-only instead of finding it closed (decision 6).
        /// </summary>
        internal void StopAfter(string operation, Exception ex)
        {
            if (WriteFailure.NamesFile(ex)) this.Disk?.RecordWriteFailure(operation, ex);
            this.Stop(ex);
        }

        /// <summary>
        /// Record a write or sync failure where stopping the engine would fail the caller's read (a
        /// <c>$database</c> read): the engine's next call stops it and reopens it read-only.
        /// </summary>
        internal void StopLater(string operation, Exception ex)
        {
            this.Disk?.RecordWriteFailure(operation, ex);
            if (this.WriteFailure != null) _stopDue = true;
        }

#if DEBUG || TESTING
        internal void CrashPoint(string phase)
        {
            AtCrashPoint?.Invoke(phase);
            SimulateProcessCrash?.Invoke(phase);
        }
#endif

        internal void Stop(Exception ex)
        {
            this.CompleteStop(ex, this.BeginStop(ex));
        }

        /// <summary>
        /// Record a write or sync failure before the stop it causes, so the engine reopens read-only
        /// instead of closing for good. The first failure wins; a shared connection keeps it for its
        /// later engines (their operations open read-only until the connection is reopened).
        /// </summary>
        internal void RecordWriteFailure(WriteFailure failure)
        {
            // Every failure is recorded before its stop: an engine that already stopped without a record
            // stopped for a failed read or a damaged file, and stays closed (implementation note 6).
            if (this.Stopped) return;
            if (Interlocked.CompareExchange(ref _writeFailure, failure, null) != null) return;
            _settings?.SharedDurability?.RecordWriteFailure(failure);
        }

        /// <summary>Wait until the failure's teardown closed the services; false after <paramref name="milliseconds"/>.</summary>
        internal bool WaitClosed(int milliseconds) => _closed.Wait(milliseconds);

        /// <summary>
        /// Make the engine immediately unusable without running cleanup that can
        /// acquire unrelated locks. The caller later owns <see cref="CompleteStop"/>.
        /// </summary>
        internal bool BeginStop(Exception ex)
        {
            // A completion that failed because the engine was already closed is not a new fatal cause.
            if (this.Disposed) return false;

            // A later completion/cleanup race must not replace the causal failure.
            return Interlocked.CompareExchange(ref _exception, ClosedEngineFailure(ex), null) == null;
        }

        /// <summary>
        /// Finish teardown for the caller that first published a fatal failure.
        /// </summary>
        internal void CompleteStop(Exception ex, bool ownsFailure)
        {
            if (!ownsFailure) return;
            try { _engine?.Close(ex, this); }
            finally
            {
                this.Disposed = true;
                _closed.Set();
#if DEBUG || TESTING
                _settings?.ReopenStage?.Invoke("stopped");
#endif
            }
        }

        /// <summary>
        /// What later calls on the closed instance throw: the cause alone reads as if it were still happening.
        /// INVALID_DATAFILE_STATE stays as is, callers match on its error code.
        /// </summary>
        private static Exception ClosedEngineFailure(Exception ex)
        {
            const string RECOVERY = "Dispose and reopen the database before retrying. ";

            if (ex is IOException) return new IOException("Engine closed after an I/O failure. " + RECOVERY + ex.Message, ex);
            if (ex is LiteException lex && (lex.ErrorCode == LiteException.INVALID_DATAFILE_STATE || lex.ErrorCode == LiteException.CHECKSUM_MISMATCH)) return ex;

            return new LiteException(LiteException.ENGINE_DISPOSED, ex, "Engine closed after a transaction completion failure. " + RECOVERY + "{0}", ex.Message);
        }

        public BsonValue ReadTransform(string collection, BsonValue value)
        {
            if (_settings?.ReadTransform is null) return value;

            var result = _settings.ReadTransform(collection, value);
            if (value is BsonDocument source && result is BsonDocument target)
                target.IsProjectionValue = source.IsProjectionValue;
            return result;
        }
    }
}
