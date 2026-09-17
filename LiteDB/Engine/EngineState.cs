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
                (ex is LiteException lex && lex.ErrorCode == LiteException.INVALID_DATAFILE_STATE))
            {
                this.Stop(ex);

                return false;
            }

            return true;
        }

        internal void Stop(Exception ex)
        {
            // A later completion/cleanup race must not replace the causal failure.
            var subsequent = ex is IOException
                ? new IOException("Engine closed after an I/O failure. Dispose and reopen the database before retrying. " + ex.Message, ex)
                : ex;
            if (Interlocked.CompareExchange(ref _exception, subsequent, null) != null) return;
            _engine?.Close(ex, this);
            this.Disposed = true;
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
