#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Power loss that drops every write a file has not synced: each file keeps the bytes it held
    /// at its last successful sync (writes after it are lost whole, not torn). Syncs of the data
    /// file (the WAL) answer EINVAL ("cannot sync", #2242) while <see cref="DataFails"/>
    /// (<see cref="LogFails"/>) is set. Tests using it belong to the NativeFileSync collection.
    /// </summary>
    internal sealed class SyncPowerLossModel : IDisposable
    {
        private readonly string _data, _log;
        private readonly object _gate = new object();
        private byte[] _durableData, _durableLog;
        internal volatile bool DataFails, LogFails;
        internal string DataFile => _data;

        internal SyncPowerLossModel(string data)
        {
            _data = Path.GetFullPath(data);
            _log = Path.GetFullPath(FileHelper.GetLogFile(data));
            _durableData = ReadShared(_data);
            _durableLog = File.Exists(_log) ? ReadShared(_log) : new byte[0];
            NativeFileSync.SimulateErrno = path =>
            {
                var name = Path.GetFullPath(path);
                if (name == _log)
                {
                    if (LogFails) return 22;
                    lock (_gate) _durableLog = ReadShared(_log);
                }
                else if (name == _data)
                {
                    if (DataFails) return 22;
                    lock (_gate) _durableData = ReadShared(_data);
                }
                return 0;
            };
        }

        /// <summary>
        /// Open the files a power loss now leaves behind, as a copy; every row of "rows" holds
        /// one value, which is returned.
        /// </summary>
        internal int AfterPowerLoss(int rows)
        {
            using var image = new TempFile();
            lock (_gate)
            {
                File.WriteAllBytes(image.Filename, _durableData);
                File.WriteAllBytes(FileHelper.GetLogFile(image.Filename), _durableLog);
            }
            try
            {
                using var db = new LiteDatabase(image.Filename);
                var values = db.GetCollection("rows").FindAll().Select(x => x["value"].AsInt32).Distinct().ToArray();
                db.GetCollection("rows").Count().Should().Be(rows);
                values.Should().HaveCount(1);
                return values[0];
            }
            finally { File.Delete(FileHelper.GetLogFile(image.Filename)); }
        }

        internal static byte[] ReadShared(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadFully(bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose() => NativeFileSync.SimulateErrno = null;
    }
}
#endif
