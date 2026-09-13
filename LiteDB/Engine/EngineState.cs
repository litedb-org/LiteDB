using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal class EngineState
    {
        public bool Disposed = false;
        private Exception _exception;
        private readonly LiteEngine _engine; // can be null for unit tests
        private readonly EngineSettings _settings;

#if DEBUG
        public Action<PageBuffer> SimulateDiskReadFail = null;
        public Action<PageBuffer> SimulateDiskWriteFail = null;
#endif

        public EngineState(LiteEngine engine, EngineSettings settings)
        {
            _engine = engine;
            _settings = settings;
        }

        public void Validate()
        {
            if (this.Disposed) throw _exception ?? LiteException.EngineDisposed();
        }

        public bool Handle(Exception ex)
        {
            LOG(ex.Message, "ERROR");

            if (ex is IOException ||
                (ex is LiteException lex && lex.ErrorCode == LiteException.INVALID_DATAFILE_STATE))
            {
                _exception = ex;

                _engine?.Close(ex);

                return false;
            }

            return true;
        }

        public BsonValue ReadTransform(string collection, BsonValue value)
        {
            if (_settings?.ReadTransform is null) return value;

            return _settings.ReadTransform(collection, value);
        }
    }
}