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

        public bool Handle(Exception ex)
        {
            LOG(ex.Message, "ERROR");

            if (ex is IOException ||
                (ex is LiteException lex && (lex.ErrorCode == LiteException.INVALID_DATAFILE_STATE || lex.ErrorCode == LiteException.CHECKSUM_MISMATCH)))
            {
                this.Stop(ex);

                return false;
            }

            return true;
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
