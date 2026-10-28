using System.IO.Compression;
using LiteDB;
using LiteDB.Engine;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

// Black-box fixtures use the persisted layout, never engine internals or hooks.
internal static class JournalRefusalProof
{
    private const int PageSize = 8192;

    internal static int Run(bool damaged)
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas")?
            .GetMethod("I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS")?.Invoke(null, null);
        var directory = Path.Combine(ReproContext.FromEnvironment().SharedDatabaseRoot ?? Path.GetTempPath(),
            "journal-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (primary, wal) = damaged ? ModernPair() : PreparedPair();
            var filename = Path.Combine(directory, "proof.db");
            var logname = Path.Combine(directory, "proof-log.db");
            foreach (var readOnly in new[] { false, true })
            {
                File.WriteAllBytes(filename, primary);
                File.WriteAllBytes(logname, wal);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var db = Open(filename, readOnly);
                    Require(db.GetCollection("rows").FindAll().Select(x => x.ToString()).SequenceEqual(new[]
                    {
                        new BsonDocument { ["_id"] = 1, ["value"] = damaged ? 42 : 7 }.ToString()
                    }), "Matching recovery control lost committed data");
                }
            }
            if (damaged)
                Put(primary, 68, BitConverter.GetBytes(BitConverter.ToInt64(primary, 68) + 1));
            else
            {
                Array.Resize(ref wal, wal.Length + PageSize);
                var start = wal.Length - PageSize;
                Put(wal, start, BitConverter.GetBytes(3u));
                wal[start + 4] = 4; // Data page.
                Put(wal, start + 14, BitConverter.GetBytes(0x7FFF0000u));
                wal[start + 31] = 0xA5;
            }
            var allFixed = true;
            var allBug = true;
            foreach (var readOnly in new[] { false, true })
            {
                File.WriteAllBytes(filename, primary);
                File.WriteAllBytes(logname, wal);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    Exception? failure = null;
                    try
                    {
                        using var db = Open(filename, readOnly);
                        if (damaged)
                        {
                            Require(db.GetCollection("rows").FindAll().Select(x => x.ToString()).SequenceEqual(new[]
                            {
                                new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString()
                            }), "Torn-header recovery lost committed rows");
                            Require(db.GetCollection("rows").Find(Query.EQ("_id", 1)).Single()["value"].AsInt32 == 42,
                                "Torn-header recovery lost indexed lookup");
                            Require(db.GetCollection("newrows").FindAll().Single()["value"].AsString == "modern",
                                "Torn-header recovery lost the WAL-only collection");
                        }
                    }
                    catch (Exception error) { failure = error; }
                    var dataSame = File.ReadAllBytes(filename).SequenceEqual(primary);
                    var logSame = File.Exists(logname) && File.ReadAllBytes(logname).SequenceEqual(wal);
                    var logLost = !File.Exists(logname) || new FileInfo(logname).Length < wal.Length;
                    host.SendLog($"readOnly={readOnly} attempt={attempt} dataSame={dataSame} logSame={logSame} logLost={logLost} error={failure}");
                    if (damaged)
                    {
                        // A checksum-invalid creation+1 is indistinguishable from
                        // a torn own header. The regression is refusing valid redo.
                        allFixed &= failure == null && (!readOnly || (dataSame && logSame));
                        allBug &= failure is LiteException refusal && refusal.ErrorCode == LiteException.INVALID_DATABASE &&
                            dataSame && logSame;
                    }
                    else
                    {
                        allFixed &= failure is LiteException refusal && refusal.ErrorCode == LiteException.INVALID_DATABASE &&
                            refusal.Message.Contains("preserve both files for recovery") && dataSame && logSame;
                        allBug &= readOnly ? failure == null && dataSame && logSame :
                            failure == null && !dataSame && !logSame && logLost;
                    }
                }
            }
            Require(allFixed || allBug, "Neither exact known-bad behavior nor preserving refusal occurred");
            var scenario = damaged ? "damaged primary identity" : "prepared journal suffix";
            var message = (allBug ? "REPRODUCED: " : "FIXED: ") + scenario;
            Console.WriteLine(message);
            host.SendResult(allBug, message);
            return allBug ? 0 : 1;
        }
        catch (Exception error)
        {
            host.SendResult(false, "INCONCLUSIVE: " + error);
            Console.Error.WriteLine(error);
            return 2;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static LiteDatabase Open(string filename, bool readOnly) => new(new ConnectionString
    {
        Filename = filename, ReadOnly = readOnly, LegacyIndexScan = readOnly
    });

    private static byte[] LegacyPrimary()
    {
        using var resource = typeof(JournalRefusalProof).Assembly.GetManifestResourceStream("LegacyWal_5_0_21.zip")!;
        using var zip = new ZipArchive(resource, ZipArchiveMode.Read);
        using var entry = zip.GetEntry("plain.db")!.Open();
        using var bytes = new MemoryStream();
        entry.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static (byte[] Primary, byte[] Wal) ModernPair()
    {
        using var data = new FailingData();
        var legacy = LegacyPrimary();
        data.Write(legacy, 0, legacy.Length);
        using var log = new MemoryStream();
        using (var engine = new LiteEngine(new EngineSettings { DataStream = data, LogStream = log }))
        using (var db = new LiteDatabase(engine, disposeOnClose: false))
        {
            db.CheckpointSize = 0;
            db.GetCollection("newrows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "modern" });
            db.Checkpoint();
            db.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            data.Armed = true;
            try { db.Checkpoint(); throw new InvalidOperationException("Checkpoint should fail"); }
            catch (IOException error) when (error.Message == "capture modern journal") { }
        }
        return (data.ToArray(), log.ToArray());
    }

    private static (byte[] Primary, byte[] Wal) PreparedPair()
    {
        var primary = LegacyPrimary();
        var wal = new byte[3 * PageSize];
        for (var page = 0; page < 3; page++)
        {
            var start = page * PageSize;
            Buffer.BlockCopy(primary, 0, wal, start, PageSize);
            Put(wal, start + 14, BitConverter.GetBytes(1u));
            wal[start + 18] = 0;
        }
        Put(wal, 132, BitConverter.GetBytes(0x324E49474542444CL)); // Header intent.
        Put(wal, 140, BitConverter.GetBytes(0L));
        Put(wal, 148, new byte[8]);
        Put(wal, 148, BitConverter.GetBytes(Crc(wal, 0, PageSize)));
        var prepared = 2 * PageSize;
        Put(wal, prepared + 132, BitConverter.GetBytes(0x315045525042444CL));
        Put(wal, prepared + 140, BitConverter.GetBytes((long)prepared));
        Put(wal, prepared + 148, new byte[4]);
        Put(wal, prepared + 152, BitConverter.GetBytes(Crc(wal, 0, prepared)));
        Put(wal, prepared + 148, BitConverter.GetBytes(Crc(wal, prepared, PageSize)));
        return (primary, wal);
    }

    private static uint Crc(byte[] bytes, int start, int count)
    {
        var crc = uint.MaxValue;
        for (var i = start; i < start + count; i++)
        {
            crc ^= bytes[i];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0x82F63B78u);
        }
        return ~crc;
    }

    private static void Put(byte[] target, int offset, byte[] value) => Buffer.BlockCopy(value, 0, target, offset, value.Length);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FailingData : MemoryStream
    {
        internal bool Armed;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Armed && count == PageSize)
            {
                Armed = false;
                throw new IOException("capture modern journal");
            }
            base.Write(buffer, offset, count);
        }
    }
}
