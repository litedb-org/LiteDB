using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3027_FailedHeaderSync;

/// <summary>
/// Defect fixed by PR #3027 (guard: SyncFailureRecovery_Tests). A sync that fails with an I/O error
/// ("fsyncgate"): Linux marks the pages it could not write back clean, so they stay in the page cache
/// without reaching the device, and a later sync of the same file succeeds without writing them. A
/// checkpoint's WAL salt rotation writes a new data header; when its sync fails, the WAL and its header
/// journal are kept, and the next open reads the new header from the cache. The known-bad recovery took
/// that header as published and retired the journal behind a data sync that wrote nothing: the device
/// kept the header from before the failed sync, and a power loss then discarded every later commit as
/// a stale WAL generation. Fixed (decision 13 of docs/decisions/durability-policy.md): recovery makes
/// the journal durable, writes the header back as it read it (the same bytes), and only then syncs and
/// retires the journal.
///
/// Black box: the database runs on caller streams (EngineSettings.DataStream/LogStream), FileStream
/// subclasses that model the device. Both LiteDB versions sync such a stream through its Flush(true)
/// (NativeFileSync: a subclass that overrides Flush(bool) defines its own sync). A write reaches the
/// file (the page cache) at once, and the device image only at a successful sync; the checkpoint's sync
/// of a lone header-page write (the salt rotation) fails with EIO and forgets its pending write, as
/// Linux does. A power loss is an open of the device images.
///
/// Exit code 0: the defect reproduced (the known-bad LiteDB must do this). Exit code 1: the fixed
/// behavior was verified in full (the candidate must do this). Exit code 2: anything else.
/// </summary>
internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int PageSize = 8192;
    private const int EIO = 5;
    private const string InjectedEio = "injected EIO: write-back failed and the pages were marked clean";

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var directory = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(), "Issue_3027_FailedHeaderSync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");
        // A development prerelease build (LITEDB_PREDEV) refuses to open files until this is acknowledged.
        engine.GetType("LiteDB.LiteDBPragmas")?.GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);

        try
        {
            var (code, summary) = Run(host, directory);
            host.SendResult(code == Reproduced, summary);
            return code;
        }
        catch (Exception error)
        {
            host.SendResult(false, $"INCONCLUSIVE: {error.GetType().Name}: {error.Message}", new { Exception = error.ToString() });
            Console.Error.WriteLine(error);
            return Inconclusive;
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host, string directory)
    {
        var acknowledged = new List<int>();
        using var data = new DeviceFile(Path.Combine(directory, "app.db"));
        using var log = new DeviceFile(Path.Combine(directory, "app-log.db"));
        var settings = new EngineSettings { DataStream = data, LogStream = log };

        // Ten rows, then a checkpoint whose salt rotation writes the new header, and that header's sync
        // fails with EIO: the rotated header stays in the page cache only, and no later sync writes it.
        using (var engine = new LiteEngine(settings))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Enumerable.Range(1, 10).Select(Row));
            acknowledged.AddRange(Enumerable.Range(1, 10));
            data.FailSync = pending => pending.Count == 1 && pending[0].Position == 0 && pending[0].Bytes?.Length == PageSize;
            try
            {
                db.Checkpoint();
                throw new InvalidOperationException("the checkpoint whose header sync failed succeeded");
            }
            catch (IOException error) when (error.ToString().Contains(InjectedEio))
            {
                host.SendLog($"The checkpoint whose header sync failed threw {error.GetType().Name}: {error.Message}");
            }
            Require(data.FailedSyncs == 1, $"{data.FailedSyncs} data syncs failed, expected the salt rotation's one");
        }
        var cached = Header(data);
        Require(!cached.Durable.SequenceEqual(cached.Live), "the rotated header reached the device although its sync failed");
        host.SendLog("After the failed sync the data file's header in the page cache differs from the one on the device");

        // The next open recovers from the header journal the failed checkpoint kept; five more rows are
        // committed and acknowledged durable.
        bool durableFlush;
        using (var engine = new LiteEngine(settings))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.CheckpointSize = 0;
            var rows = db.GetCollection("rows");
            Require(rows.Count() == 10, $"the reopened database holds {rows.Count()} rows, expected 10");
            rows.Insert(Enumerable.Range(11, 5).Select(Row));
            acknowledged.AddRange(Enumerable.Range(11, 5));
            durableFlush = db.Execute("SELECT $ FROM $database").Single()["durableLogFlush"].AsBoolean;
        }
        Require(durableFlush, "the commits after the recovery were not acknowledged durable (durableLogFlush=false)");
        var header = Header(data);
        var stale = !header.Durable.SequenceEqual(header.Live);
        host.SendLog($"After the recovering open and its commits the device holds {(stale ? "the header from before the failed sync" : "the header the engine reads")}");

        // A power loss: only what reached the device is left.
        var (recovered, later) = Recover(directory, data.Durable, log.Durable);
        host.SendLog($"Acknowledged [{Ids(acknowledged)}], recovered after the power loss [{Ids(recovered)}]");

        if (acknowledged.Except(recovered).Any())
        {
            Require(!recovered.Except(acknowledged).Any(), $"recovered {Ids(recovered)}, more than the acknowledged {Ids(acknowledged)}");
            Require(stale, "rows were lost although the device holds the header the engine reads");
            return (Reproduced, "REPRODUCED: commits acknowledged durable after the recovery of a failed header sync were lost at a power loss " +
                $"(acknowledged [{Ids(acknowledged)}], recovered [{Ids(recovered)}]): recovery retired the header journal behind a sync " +
                "that wrote nothing, and the device kept the header from before the failed sync");
        }

        Require(recovered.SequenceEqual(acknowledged), $"recovered {Ids(recovered)}, expected {Ids(acknowledged)}");
        Require(!stale, "the device holds a header other than the one the engine reads");
        Require(later, "the recovered database did not take and keep a later commit");
        return (Fixed, "FIXED: after a failed header sync, recovery wrote the header back before the sync that retired its journal: " +
            $"the device holds the header the engine reads, the power loss kept every acknowledged row [{Ids(acknowledged)}], " +
            "and the recovered database took and kept a new commit");
    }

    /// <summary>The rows opened from the device images, and whether the database then takes and keeps a new commit.</summary>
    private static (List<int> Ids, bool Later) Recover(string directory, byte[] data, byte[] log)
    {
        var image = Path.Combine(directory, "power-loss");
        Directory.CreateDirectory(image);
        var (dataPath, logPath) = (Path.Combine(image, "app.db"), Path.Combine(image, "app-log.db"));
        File.WriteAllBytes(dataPath, data);
        File.WriteAllBytes(logPath, log);

        List<int> ids;
        // The engine does not own caller streams: they are disposed after it.
        using (var dataStream = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite))
        using (var logStream = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite))
        using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = dataStream, LogStream = logStream })))
        {
            var docs = db.GetCollection("rows").FindAll().ToList();
            ids = docs.Select(x => x["_id"].AsInt32).OrderBy(x => x).ToList();
            if (!docs.All(x => x["payload"].AsString == Row(x["_id"].AsInt32)["payload"].AsString))
                throw new InvalidOperationException("a recovered row does not hold the payload it was committed with");
            if (!ids.SequenceEqual(Enumerable.Range(1, 15))) return (ids, false);
            db.CheckpointSize = 0;
            db.GetCollection("rows").Insert(Row(16));
        }
        using (var dataStream = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite))
        using (var logStream = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite))
        using (var db = new LiteDatabase(new LiteEngine(new EngineSettings { DataStream = dataStream, LogStream = logStream })))
        {
            var later = db.GetCollection("rows").FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x);
            return (ids, later.SequenceEqual(Enumerable.Range(1, 16)));
        }
    }

    private static (byte[] Durable, byte[] Live) Header(DeviceFile file) =>
        (file.Durable.Take(PageSize).ToArray(), file.Live.Take(PageSize).ToArray());

    private static string Ids(IEnumerable<int> ids)
    {
        var sorted = ids.OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j - i >= 2 ? $"{sorted[i]}-{sorted[j]}" : string.Join(", ", sorted.Skip(i).Take(j - i + 1)));
            i = j + 1;
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["payload"] = new string((char)('a' + id % 26), 500) };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// A file on a device: a write reaches the file at once (the page cache, what every reader sees)
    /// and the device image only at the next successful sync. A sync that <see cref="FailSync"/>
    /// selects fails with EIO and forgets the writes pending since the last successful sync, as Linux
    /// does ("fsyncgate"): they stay in the cache, and no later sync writes them.
    /// </summary>
    private sealed class DeviceFile : FileStream
    {
        private readonly object _gate = new();
        // Writes (a copy of their bytes) and SetLength calls (Bytes null) since the last successful sync.
        private readonly List<(long Position, byte[]? Bytes, long Length)> _pending = new();
        private byte[] _durable;

        /// <summary>Selects, from the pending writes, the one sync that fails with EIO.</summary>
        internal Func<List<(long Position, byte[]? Bytes, long Length)>, bool>? FailSync;
        internal int FailedSyncs;

        internal DeviceFile(string path)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1)
        {
            _durable = ReadShared(path);
        }

        /// <summary>The bytes on the device: as of the last successful sync.</summary>
        internal byte[] Durable { get { lock (_gate) return _durable; } }

        /// <summary>The bytes in the page cache.</summary>
        internal byte[] Live => ReadShared(Name);

        public override void Write(byte[] buffer, int offset, int count)
        {
            var at = Position;
            base.Write(buffer, offset, count);
            lock (_gate) _pending.Add((at, buffer.AsSpan(offset, count).ToArray(), -1));
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void WriteByte(byte value) => Write(new[] { value }, 0, 1);

        public override void SetLength(long value)
        {
            base.SetLength(value);
            lock (_gate) _pending.Add((0, null, value));
        }

        public override void Flush(bool flushToDisk)
        {
            base.Flush(false);
            if (!flushToDisk) return;
            lock (_gate)
            {
                if (FailSync?.Invoke(_pending) == true)
                {
                    FailSync = null;
                    FailedSyncs++;
                    _pending.Clear();
                    throw new IOException(InjectedEio, EIO);
                }
                var image = (byte[])_durable.Clone();
                foreach (var (position, bytes, length) in _pending)
                {
                    if (bytes == null)
                    {
                        Array.Resize(ref image, (int)length);
                        continue;
                    }
                    if (position + bytes.Length > image.Length) Array.Resize(ref image, (int)(position + bytes.Length));
                    Buffer.BlockCopy(bytes, 0, image, (int)position, bytes.Length);
                }
                _durable = image;
                _pending.Clear();
            }
        }

        private static byte[] ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }
}
