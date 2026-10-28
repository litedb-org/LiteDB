using System.IO.Compression;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

// Black-box fixtures use the persisted layout, never engine internals or hooks.
internal static class MultifieldHeaderTearProof
{
    private const int PageSize = 8192;

    internal static int Run()
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
            var (primary, wal) = ModernPair();
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
                        new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString()
                    }), "Matching recovery control lost committed data");
                }
            }
            // Only clear bits/bytes: erase version/markers/CRC together.
            primary[59] &= 8;
            primary[31] = 0;
            Array.Clear(primary, 125, 4);
            Array.Clear(primary, 14, 4);
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
                        Require(db.GetCollection("rows").FindAll().Select(x => x.ToString()).SequenceEqual(new[]
                        {
                            new BsonDocument { ["_id"] = 1, ["value"] = 42 }.ToString()
                        }), "Torn-header recovery lost committed rows");
                        Require(db.GetCollection("rows").Find(Query.EQ("_id", 1)).Single()["value"].AsInt32 == 42,
                            "Torn-header recovery lost indexed lookup");
                        Require(db.GetCollection("newrows").FindAll().Single()["value"].AsString == "modern",
                            "Torn-header recovery lost the WAL-only collection");
                    }
                    catch (Exception error) { failure = error; }
                    var dataSame = File.ReadAllBytes(filename).SequenceEqual(primary);
                    var logSame = File.Exists(logname) && File.ReadAllBytes(logname).SequenceEqual(wal);
                    host.SendLog($"readOnly={readOnly} attempt={attempt} dataSame={dataSame} logSame={logSame} error={failure}");
                    allFixed &= failure == null && (!readOnly || (dataSame && logSame));
                    allBug &= failure is LiteException refusal && refusal.ErrorCode == LiteException.INVALID_DATABASE &&
                        dataSame && logSame;
                }
            }
            Require(allFixed || allBug, "Neither exact known-bad refusal nor complete recovery occurred");
            var scenario = "multifield header tear";
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
        using var resource = typeof(MultifieldHeaderTearProof).Assembly.GetManifestResourceStream("LegacyWal_5_0_21.zip")!;
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
