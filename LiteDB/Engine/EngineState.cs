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
        private Exception _exception;
        private readonly LiteEngine _engine; // can be null for unit tests
        private readonly EngineSettings _settings;

#if DEBUG || TESTING
        public Action<PageBuffer> SimulateDiskReadFail = null;
        public Action<PageBuffer> SimulateDiskWriteFail = null;
        internal Action<PageBuffer> SimulateDataWriteFail;
#endif

        public EngineState(LiteEngine engine, EngineSettings settings)
        {
            _engine = engine;
            _settings = settings;
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

        internal void Stop(Exception ex)
        {
            // A completion that failed because the engine was already closed is not a new fatal cause.
            if (this.Disposed) return;

            // A later completion/cleanup race must not replace the causal failure.
            if (Interlocked.CompareExchange(ref _exception, ClosedEngineFailure(ex), null) != null) return;
            _engine?.Close(ex, this);
            this.Disposed = true;
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
