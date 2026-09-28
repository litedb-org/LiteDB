#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using System.Threading;
using LiteDB.Engine;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// <see cref="SyncPowerLossModel"/> for engines that open the database files themselves: each
    /// file keeps the bytes it held at its last successful sync, observed through the NativeFileSync
    /// hook. Syncs of the data file answer EINVAL ("cannot sync", #2242) while
    /// <see cref="DataFails"/> is set. Tests using it belong to the NativeFileSync collection.
    /// </summary>
    internal sealed class FilePowerLossModel : IDisposable
    {
        private readonly string _data, _log;
        private readonly object _gate = new object();
        private byte[] _durableData, _durableLog;
        private int _dataSyncs;
        internal volatile bool DataFails;

        internal FilePowerLossModel(string filename)
        {
            _data = Path.GetFullPath(filename);
            _log = Path.GetFullPath(FileHelper.GetLogFile(filename));
            _durableData = Read(_data);
            _durableLog = Read(_log);
            NativeFileSync.SimulateErrno = path =>
            {
                var name = Path.GetFullPath(path);
                if (string.Equals(name, _log, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_gate) _durableLog = Read(_log);
                }
                else if (string.Equals(name, _data, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _dataSyncs);
                    if (DataFails) return 22;
                    lock (_gate) _durableData = Read(_data);
                }
                return 0;
            };
        }

        /// <summary>Syncs of the data file attempted so far.</summary>
        internal int DataSyncs => Volatile.Read(ref _dataSyncs);

        /// <summary>Open the files a power loss now leaves behind, as a copy, and read them.</summary>
        internal T AfterPowerLoss<T>(Func<LiteDatabase, T> read)
        {
            using var image = new TempFile();
            lock (_gate)
            {
                File.WriteAllBytes(image.Filename, _durableData);
                File.WriteAllBytes(FileHelper.GetLogFile(image.Filename), _durableLog);
            }
            var hook = NativeFileSync.SimulateErrno;
            NativeFileSync.SimulateErrno = null;
            try
            {
                using var db = new LiteDatabase(image.Filename);
                return read(db);
            }
            finally
            {
                NativeFileSync.SimulateErrno = hook;
                File.Delete(FileHelper.GetLogFile(image.Filename));
            }
        }

        private static byte[] Read(string filename)
        {
            if (!File.Exists(filename)) return new byte[0];
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadFully(bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose() => NativeFileSync.SimulateErrno = null;
    }
}
#endif
