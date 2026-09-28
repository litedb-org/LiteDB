#if DEBUG || TESTING
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Internals;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Power loss that drops every write a file has not synced: each file keeps the bytes it held
    /// at its last successful sync (writes after it are lost whole, not torn). Engines use the
    /// data file and its WAL as caller streams (<see cref="Settings"/>), whose Flush(true) the
    /// engine calls for every device sync on every platform; it answers "cannot sync" (#2242)
    /// while <see cref="DataFails"/> (<see cref="LogFails"/>) is set.
    /// </summary>
    internal sealed class SyncPowerLossModel : IDisposable
    {
        private readonly string _filename;
        private readonly DurableFile _data, _log;

        internal SyncPowerLossModel(string filename)
        {
            _filename = filename;
            _data = new DurableFile(filename);
            _log = new DurableFile(FileHelper.GetLogFile(filename));
        }

        internal bool DataFails { get => _data.Fails; set => _data.Fails = value; }

        /// <summary>A checkpoint stage at which the data file stops syncing (see <see cref="Settings"/>).</summary>
        internal volatile string RetirementStage;
        internal bool LogFails { get => _log.Fails; set => _log.Fails = value; }

        /// <summary>Device syncs attempted on the data file and on the WAL so far.</summary>
        internal int DataSyncs => _data.Syncs;
        internal int LogSyncs => _log.Syncs;
        internal string DataFile => _filename;

        /// <summary>Settings of an engine (or a shared connection) over the two files.</summary>
        internal EngineSettings Settings() => new EngineSettings
        {
            Filename = _filename, DataStream = _data, LogStream = _log,
            CheckpointStage = stage => { if (stage == RetirementStage) DataFails = true; }
        };

        /// <summary>
        /// Open the files a power loss now leaves behind, as a copy; every row of "rows" holds
        /// one value, which is returned.
        /// </summary>
        internal int AfterPowerLoss(int rows)
        {
            using var image = new TempFile();
            File.WriteAllBytes(image.Filename, _data.Durable);
            File.WriteAllBytes(FileHelper.GetLogFile(image.Filename), _log.Durable);
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

        /// <summary>
        /// Open the files a power loss now leaves behind, as a copy, and check them with
        /// <see cref="AssertRows"/>; returns the value every row holds.
        /// </summary>
        internal int AssertAfterPowerLoss(int rows, int? value = null) =>
            FilePowerLossModel.Open(this.Capture(), db => AssertRows(db, rows, value));

        /// <summary>
        /// Exact state: "rows" holds ids 1..<paramref name="count"/>, each
        /// <see cref="MvccRetirementScenario.Document"/> of <paramref name="value"/> (of the first
        /// row's value when none is given) byte for byte, and a query on value finds each (through
        /// the value index where the test created one). Returns the value.
        /// </summary>
        internal static int AssertRows(LiteDatabase db, int count, int? value = null)
        {
            var rows = db.GetCollection("rows");
            var all = rows.FindAll().OrderBy(x => x["_id"].AsInt32).ToArray();
            all.Should().HaveCount(count);
            var expected = value ?? all[0]["value"].AsInt32;
            all.Should().BeEquivalentTo(Enumerable.Range(1, count).Select(id => MvccRetirementScenario.Document(id, expected)),
                o => o.WithStrictOrdering());
            rows.Count().Should().Be(count);
            rows.Find(Query.EQ("value", expected)).Select(x => x["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(1, count));
            rows.Count(Query.Not("value", expected)).Should().Be(0);
            return expected;
        }

        /// <summary>The files a power loss would leave behind now.</summary>
        internal (byte[] Data, byte[] Log) Capture() => (_data.Durable, _log.Durable);

        internal static byte[] ReadShared(string filename)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadFully(bytes, 0, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            _data.Dispose();
            _log.Dispose();
        }

        /// <summary>An unbuffered file whose successful device sync records its content.</summary>
        private sealed class DurableFile : FileStream
        {
            private readonly object _gate = new object();
            private byte[] _durable;
            internal volatile bool Fails;
            private int _syncs;

            internal int Syncs => System.Threading.Volatile.Read(ref _syncs);

            internal DurableFile(string path)
                : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1)
            {
                _durable = ReadShared(path);
            }

            internal byte[] Durable { get { lock (_gate) return _durable; } }

            public override void Flush(bool flushToDisk)
            {
                if (!flushToDisk)
                {
                    base.Flush(false);
                    return;
                }
                System.Threading.Interlocked.Increment(ref _syncs);
                if (Fails)
                {
                    base.Flush(false);
                    throw new UnauthorizedAccessException("sync unsupported");
                }
                base.Flush(true);
                lock (_gate) _durable = ReadShared(this.Name);
            }
        }
    }
}
#endif
