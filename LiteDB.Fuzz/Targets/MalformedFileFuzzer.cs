using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class MalformedFileFuzzer : IFuzzTarget
{
    public string Name => "malformed-file";
    public string Description => "Grammar-aware header/page/WAL corruption and truncation with public-failure and pristine-usability oracles.";

    public Task RunAsync(FuzzContext context)
    {
        var baseline = Path.Combine(context.DirectoryPath, "malformed-baseline.db");
        var encryptedBaseline = Path.Combine(context.DirectoryPath, "malformed-encrypted.db");
        CreateBaseline(baseline, null);
        CreateBaseline(encryptedBaseline, "malformed-password");
        var baselineLog = Path.ChangeExtension(baseline, null) + "-log.db";
        var encryptedLog = Path.ChangeExtension(encryptedBaseline, null) + "-log.db";
        var rejected = 0;
        while (context.Next())
        {
            var kind = (context.Steps - 1) % 12;
            var encrypted = kind == 11;
            var source = encrypted ? encryptedBaseline : baseline;
            var sourceLog = encrypted ? encryptedLog : baselineLog;
            var password = encrypted ? "malformed-password" : null;
            var file = context.RegisterFile(Path.Combine(context.DirectoryPath, $"malformed-{context.Steps}.db"));
            var log = Path.ChangeExtension(file, null) + "-log.db";
            File.Copy(source, file, true);
            if (File.Exists(sourceLog)) File.Copy(sourceLog, log, true);
            Mutate(file, log, kind, context.Random);
            Exception failure = null;
            var accepted = false;
            try
            {
                using var corrupted = Open(file, password);
                var rows = corrupted.GetCollection("rows").FindAll().ToArray();
                context.Check(rows.Length == 80, "Accepted malformed file did not recover the exact logical state.");
                corrupted.Checkpoint();
                DatabaseIntegrityVerifier.Verify(context, file, password);
                accepted = true;
            }
            catch (Exception error) { failure = error; }
            context.Check(accepted || failure is LiteException or IOException or InvalidDataException or ArgumentException or
                InvalidOperationException or System.Text.DecoderFallbackException or FuzzFailureException,
                $"Malformed file escaped through {failure?.GetType().FullName ?? "no rejection"}.");
            if (!accepted) rejected++;

            using (var pristine = Open(source, password))
                context.Check(pristine.GetCollection("rows").Count() == 80, "Malformed probe damaged its pristine source.");
            context.ObserveNovelty("malformed-file", kind, encrypted,
                accepted ? "wal-healed" : failure.GetType().Name);
        }
        context.Metrics["malformedFileRejections"] = rejected;
        return Task.CompletedTask;
    }

    private static void Mutate(string data, string log, int kind, Random random)
    {
        var path = kind is >= 7 and <= 10 && File.Exists(log) && new FileInfo(log).Length != 0 ? log : data;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        switch (kind)
        {
            case 0: Write(stream, HeaderPage.P_HEADER_INFO, 0); break;
            case 1: Write(stream, HeaderPage.P_FILE_VERSION, 255); break;
            case 2: stream.SetLength(Math.Max(1, stream.Length - 1)); break;
            case 3: Write(stream, HeaderPage.P_LAST_PAGE_ID, uint.MaxValue); break;
            case 4: Write(stream, Constants.PAGE_SIZE + BasePage.P_PAGE_TYPE, 255); break;
            case 5: Write(stream, Constants.PAGE_SIZE + BasePage.P_ITEMS_COUNT, 255); break;
            case 6: Write(stream, Constants.PAGE_SIZE + BasePage.P_USED_BYTES, ushort.MaxValue); break;
            case 7: stream.SetLength(Math.Max(1, stream.Length - 1 - random.Next(32))); break;
            case 8: Write(stream, BasePage.P_IS_CONFIRMED, 2); break;
            case 9: Write(stream, BasePage.P_TRANSACTION_ID, uint.MaxValue); break;
            case 10: DuplicateFirstPage(stream); break;
            case 11: Write(stream, 0, 0x7f); break;
        }
        stream.Flush(true);
    }

    private static void CreateBaseline(string file, string password)
    {
        using var db = Open(file, password);
        db.CheckpointSize = 0;
        var rows = db.GetCollection("rows");
        rows.EnsureIndex("value", "value");
        rows.InsertBulk(Enumerable.Range(1, 80).Select(id => new BsonDocument
        {
            ["_id"] = id, ["value"] = id % 11, ["payload"] = new byte[500 + id * 17]
        }));
    }

    private static LiteDatabase Open(string file, string password) => new(new ConnectionString
    { Filename = file, Password = password });

    private static void DuplicateFirstPage(FileStream stream)
    {
        if (stream.Length < Constants.PAGE_SIZE * 2L)
        {
            stream.SetLength(Math.Max(1, stream.Length - 1));
            return;
        }
        var page = new byte[Constants.PAGE_SIZE];
        stream.Position = 0;
        stream.ReadExactly(page);
        stream.Position = Constants.PAGE_SIZE;
        stream.Write(page);
    }

    private static void Write(FileStream stream, long offset, byte value)
    { stream.Position = offset; stream.WriteByte(value); }
    private static void Write(FileStream stream, long offset, ushort value)
    { stream.Position = offset; stream.Write(BitConverter.GetBytes(value)); }
    private static void Write(FileStream stream, long offset, uint value)
    { stream.Position = offset; stream.Write(BitConverter.GetBytes(value)); }
}
