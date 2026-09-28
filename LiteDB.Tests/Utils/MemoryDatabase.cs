using System;
using System.IO;
using LiteDB.Engine;

namespace LiteDB.Tests
{
    // Keep serialized data across sequential engine instances without using files.
    internal sealed class MemoryDatabase : IDisposable
    {
        private readonly MemoryStream _data = new MemoryStream();
        private readonly MemoryStream _log = new MemoryStream();
        private readonly MemoryStream _temp = new MemoryStream();

        internal LiteDatabase Open(BsonMapper mapper = null)
        {
            return new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = _data,
                LogStream = _log,
                TempStream = _temp
            }), mapper);
        }

        public void Dispose()
        {
            _temp.Dispose();
            _log.Dispose();
            _data.Dispose();
        }
    }
}
