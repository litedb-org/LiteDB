using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

// Small in-memory images only. Persist the current probe before checking it so
// failed epochs retain the exact data/WAL even when encryption uses a random IV.
internal sealed class ChecksumFixture : IDisposable
{
    internal const int PageSize = 8192;
    internal MemoryStream Data { get; } = new();
    internal MemoryStream Log { get; } = new();
    internal string Password { get; }
    internal SortedDictionary<int, BsonDocument> Rows { get; } = new();
    internal BsonDocument Cold { get; } = new() { ["_id"] = 1, ["payload"] = new string('c', 12000) };

    internal ChecksumFixture(FuzzContext context)
    {
        Password = context.Random.Next(2) == 0 ? null : "checksum-fuzz";
        using var db = Open(Data, Log, Password);
        db.CheckpointSize = 0;
        db.GetCollection("rows").EnsureIndex("value");
        db.GetCollection("cold").Insert(Clone(Cold));
        var count = context.Random.Next(4, 15);
        for (var id = 1; id <= count; id++)
        {
            var row = Document(context.Random, id);
            Rows[id] = row;
            db.GetCollection("rows").Insert(Clone(row));
        }
        db.Checkpoint();
    }

    internal static LiteDatabase Open(Stream data, Stream log, string password, bool readOnly = false) =>
        new(new LiteEngine(new EngineSettings
        {
            CompactStorage = CompactStorageMode.Legacy, DataStream = data, LogStream = log, Password = password, ReadOnly = readOnly,
            DurableCommits = true, TransactionPageLimit = 1
        }));

    internal static MemoryStream Copy(byte[] bytes)
    {
        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;
        return stream;
    }

    internal static BsonDocument Document(Random random, int id) => new()
    {
        ["_id"] = id, ["value"] = random.Next(-20, 21),
        ["payload"] = new string((char)random.Next('a', 'z' + 1), random.Next(3) == 0 ? random.Next(8200, 19000) : random.Next(1, 1200))
    };

    internal static BsonDocument Clone(BsonDocument row) => BsonSerializer.Deserialize(BsonSerializer.Serialize(row));
    internal static bool Equal(BsonDocument left, BsonDocument right) =>
        BsonSerializer.Serialize(left).SequenceEqual(BsonSerializer.Serialize(right));

    internal static void Verify(FuzzContext context, LiteDatabase db, SortedDictionary<int, BsonDocument> model, BsonDocument cold)
    {
        var rows = db.GetCollection("rows");
        var actual = rows.FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
        context.Check(actual.Length == model.Count && actual.Zip(model.Values, Equal).All(equal => equal),
            "Recovered documents differ from the complete acknowledged model.");
        foreach (var value in model.Values.Select(row => row["value"].AsInt32).Distinct())
        {
            var indexed = rows.Find(Query.EQ("value", value)).OrderBy(row => row["_id"].AsInt32).ToArray();
            var expected = model.Values.Where(row => row["value"].AsInt32 == value).ToArray();
            context.Check(indexed.Length == expected.Length && indexed.Zip(expected, Equal).All(equal => equal),
                "Recovered secondary-index results differ from the model.");
        }
        context.Check(Equal(db.GetCollection("cold").FindById(1), cold), "Untouched legacy collection changed.");
    }

    internal static byte[] Plain(MemoryStream physical, string password)
    {
        using var factory = new StreamFactory(physical, password);
        using var stream = factory.GetStream(false, false);
        var result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        return result;
    }

    internal static void Replace(MemoryStream physical, byte[] plain, string password)
    {
        using var factory = new StreamFactory(physical, password);
        using var stream = factory.GetStream(true, false);
        stream.Position = 0;
        stream.Write(plain, 0, plain.Length);
        stream.SetLength(plain.Length);
        stream.FlushToDisk();
    }

    internal void MakeLegacy(byte version)
    {
        var data = Plain(Data, Password);
        var log = Plain(Log, Password);
        for (var offset = 0; offset < data.Length; offset += PageSize) Legacy(data, offset, version);
        using var oldLog = new MemoryStream();
        for (var offset = 0; offset < log.Length; offset += WalChecksum.FrameSize)
        {
            Legacy(log, offset, version);
            oldLog.Write(log, offset, PageSize);
        }
        Replace(Data, data, Password);
        Replace(Log, oldLog.ToArray(), Password);
    }

    private static void Legacy(byte[] bytes, int offset, byte version)
    {
        bytes[offset + 31] = 0;
        if (BitConverter.ToUInt32(bytes, offset) != 0) return;
        bytes[offset + HeaderPage.P_FILE_VERSION] = version;
        Array.Clear(bytes, offset + WalChecksum.MarkerPosition, 4);
    }

    internal static void Save(FuzzContext context, byte[] data, byte[] log)
    {
        File.WriteAllBytes(context.RegisterFile(Path.Combine(context.DirectoryPath, "probe.db")), data);
        File.WriteAllBytes(context.RegisterFile(Path.Combine(context.DirectoryPath, "probe-log.db")), log);
    }

    // Independent, deliberately slow bitwise CRC32C oracle (no engine CRC calls).
    internal static uint Crc(byte[] bytes, int offset, int count, int zeroStart, int zeroLength)
    {
        uint crc = uint.MaxValue;
        for (var i = 0; i < count; i++)
        {
            crc ^= i >= zeroStart && i < zeroStart + zeroLength ? (byte)0 : bytes[offset + i];
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0x82F63B78u);
        }
        return ~crc;
    }

    internal static void Audit(FuzzContext context, MemoryStream data, string password)
    {
        var bytes = Plain(data, password);
        var last = BitConverter.ToUInt32(bytes, HeaderPage.P_LAST_PAGE_ID);
        var boundary = BitConverter.ToUInt32(bytes, 161);
        var mixed = bytes[160] == 0xA5;
        context.Check(mixed || bytes[160] == 0x5A, "Unknown coverage marker.");
        context.Check(boundary <= last && (mixed || boundary == 0), "Invalid legacy boundary.");
        for (var id = 0; id <= last; id++)
        {
            var offset = checked((int)id * PageSize);
            context.Check(BitConverter.ToUInt32(bytes, offset) == id, "Page identity differs from its physical position.");
            if (id > 0 && mixed && id <= boundary && bytes[offset + 31] == 0) continue;
            context.Check(bytes[offset + 31] == 0xA5 &&
                BitConverter.ToUInt32(bytes, offset + 14) == Crc(bytes, offset, PageSize, 14, 4),
                "Page marker or independent CRC32C is invalid.");
        }
    }

    public void Dispose() { Data.Dispose(); Log.Dispose(); }
}
