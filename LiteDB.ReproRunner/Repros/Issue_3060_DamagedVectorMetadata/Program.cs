using System.Reflection;
using LiteDB;
using LiteDB.Vector;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_3060_DamagedVectorMetadata;

internal static class Program
{
    private const int Reproduced = 0;
    private const int Fixed = 1;
    private const int Inconclusive = 2;
    private const int PageSize = 8192;
    private const int CollectionPage = 2;
    private const int TransactionId = 14;
    private const int Confirmed = 18;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();
        var path = Path.Combine(context.SharedDatabaseRoot ?? Path.GetTempPath(),
            "Issue_3060_DamagedVectorMetadata-" + Guid.NewGuid().ToString("N") + ".db");
        var engine = typeof(LiteDatabase).Assembly;
        host.SendLog($"LiteDB {engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} loaded from {engine.Location}");

        try
        {
            var (code, summary) = Run(host, path);
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
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path))); } catch (IOException) { }
        }
    }

    private static (int Code, string Summary) Run(ReproHostClient host, string path)
    {
        using (var db = new LiteDatabase(path))
        {
            var docs = db.GetCollection("docs");
            docs.EnsureIndex("embedding", "$.Embedding", new VectorIndexOptions(2, VectorDistanceMetric.Euclidean));
            docs.Insert(new BsonDocument { ["_id"] = 1, ["Embedding"] = new BsonVector(new[] { 100f, 0f }) });
            docs.Insert(new BsonDocument { ["_id"] = 2, ["Embedding"] = new BsonVector(new[] { 1f, 1f }) });
            Require(docs.Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray().Single()["_id"].AsInt32 == 2,
                "the healthy Euclidean index did not return the expected nearest row");
            db.Checkpoint();
        }

        var beforeLength = new FileInfo(path).Length;
        CorruptVectorMetadata(path);
        var damaged = File.ReadAllBytes(path);
        Require(damaged.LongLength == beforeLength, "the corruption image changed the file length");

        var logPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-log" + Path.GetExtension(path));
        var damagedLog = ReadBytes(logPath);
        var wrongMetric = false;
        var admittedWrite = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using (var db = new LiteDatabase(path))
            {
                try
                {
                    var row = db.GetCollection("docs").Query().TopKNear("Embedding", new[] { 1f, 0f }, 1).ToArray().Single()["_id"].AsInt32;
                    Require(row == 1, "damaged metadata neither refused nor reproduced the cosine wrong row");
                    wrongMetric = true;
                    host.SendLog($"Attempt {attempt}: Euclidean expected row 2; damaged metadata returned cosine row 1");
                }
                catch (LiteException error) { RequireRefusal(error); }
            }
            if (!wrongMetric && !admittedWrite)
            {
                Require(File.ReadAllBytes(path).SequenceEqual(damaged), "query refusal changed the damaged data file");
                Require(ReadBytes(logPath).SequenceEqual(damagedLog), "query refusal changed the recovery WAL");
            }
            using (var db = new LiteDatabase(path))
            {
                try
                {
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 3 + attempt, ["Embedding"] = new BsonVector(new[] { 100f, 0f }) });
                    admittedWrite = true;
                    host.SendLog($"Attempt {attempt}: insert acknowledged despite damaged vector metadata");
                }
                catch (LiteException error) { RequireRefusal(error); }
            }
            if (!wrongMetric && !admittedWrite)
            {
                Require(File.ReadAllBytes(path).SequenceEqual(damaged), "refusal changed the damaged data file");
                Require(ReadBytes(logPath).SequenceEqual(damagedLog), "refusal changed the recovery WAL");
            }
        }
        if (wrongMetric && admittedWrite)
            return (Reproduced, "REPRODUCED: damaged metadata changed the vector metric and acknowledged writes");
        Require(!wrongMetric && !admittedWrite, "only one unsafe path reproduced");
        return (Fixed, "FIXED: damaged metadata refused without changing the files");
    }

    private static byte[] ReadBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();

    private static void RequireRefusal(LiteException error)
    {
        Require(error.ErrorCode == LiteException.INVALID_DATAFILE_STATE &&
            error.Message.Contains("docs") && error.Message.Contains("embedding") &&
            error.Message.Contains("Rebuild"),
            "refusal must identify the collection/index and the rebuild remedy: " + error);
    }

    private static void CorruptVectorMetadata(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var needle = System.Text.Encoding.UTF8.GetBytes("embedding\0");
        var found = new List<int>();
        for (var i = 0; i <= bytes.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++) if (bytes[i + j] != needle[j]) { match = false; break; }
            if (match) found.Add(i);
        }
        Require(found.Count >= 2, "the current vector page did not contain index and metadata names");
        var position = found[1];
        var pageStart = position / PageSize * PageSize;
        Require(bytes[pageStart + 4] == CollectionPage, "the vector metadata name was not in a collection page");
        bytes[position] = 0xFF;
        WriteUInt32(bytes, pageStart + TransactionId, ComputeCrc(bytes, pageStart));
        File.WriteAllBytes(path, bytes);
    }

    private static uint ComputeCrc(byte[] bytes, int page)
    {
        var crc = uint.MaxValue;
        crc = Update(crc, bytes, page, TransactionId);
        crc = Update(crc, new byte[4], 0, 4);
        crc = Update(crc, bytes, page + Confirmed, PageSize - Confirmed);
        return ~crc;
    }

    private static uint Update(uint crc, byte[] bytes, int offset, int count)
    {
        for (var i = offset; i < offset + count; i++)
        {
            var value = crc ^ bytes[i];
            for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0x82F63B78u;
            crc = value;
        }
        return crc;
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
