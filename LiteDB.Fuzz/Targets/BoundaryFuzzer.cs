using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB.Fuzz.Targets;

internal sealed class BoundaryFuzzer : IFuzzTarget
{
    public string Name => "boundary";
    public string Description => "Exact slot, key, document, page, transaction, header, and nesting boundaries.";

    public Task RunAsync(FuzzContext context)
    {
        if (!context.Next()) return Task.CompletedTask;
        VerifyPageSlots(context);
        VerifyIndexKeys(context);
        VerifyDocumentSizes(context);
        VerifyTransactionPageLimits(context);
        VerifyCollectionIndexSlots(context);
        VerifyAllocationFailure(context);
        VerifyNesting(context);
        context.ObserveNovelty("exact-boundaries", 255, MAX_INDEX_KEY_LENGTH,
            DataService.MAX_DATA_BYTES_PER_PAGE, MAX_DOCUMENT_SIZE);
        context.Metrics["explicitBoundaries"] = 7;
        return Task.CompletedTask;
    }

    private static void VerifyPageSlots(FuzzContext context)
    {
        var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 1) { ShareCounter = BUFFER_WRITABLE };
        try
        {
            var page = new BasePage(buffer, 1, PageType.Empty);
            for (var slot = 0; slot < byte.MaxValue; slot++) page.Insert(1, out _);
            context.Check(page.ItemsCount == 255 && page.HighestIndex == 254 && page.FreeBytes == 0,
                "A page did not represent the 255-slot boundary exactly.");
            var rejected = false;
            try { page.Insert(1, out _); }
            catch (Exception error) when (CleanFailure(error)) { rejected = true; }
            context.Check(rejected, "A page accepted a 256th slot.");
        }
        finally { buffer.ShareCounter = 0; }
    }

    private static void VerifyIndexKeys(FuzzContext context)
    {
        foreach (var expectedLength in new[] { MAX_INDEX_KEY_LENGTH - 1, MAX_INDEX_KEY_LENGTH, MAX_INDEX_KEY_LENGTH + 1 })
        {
            var key = StringKey(expectedLength);
            context.Check(IndexNode.GetKeyLength(key, false) == expectedLength,
                "Could not construct the requested exact index-key length.");
            var file = Path.Combine(context.DirectoryPath, $"key-{expectedLength}.db");
            var accepted = false;
            using (var db = new LiteDatabase(file))
            {
                var rows = db.GetCollection("rows");
                rows.EnsureIndex("key", "Key");
                try
                {
                    rows.Insert(new BsonDocument { ["_id"] = 1, ["Key"] = key });
                    accepted = true;
                }
                catch (Exception error) when (CleanFailure(error)) { }
                db.Checkpoint();
            }
            context.Check(accepted == (expectedLength <= MAX_INDEX_KEY_LENGTH),
                $"Index-key boundary {expectedLength} had the wrong success result.");
            DatabaseIntegrityVerifier.Verify(context, file);
        }
    }

    private static void VerifyDocumentSizes(FuzzContext context)
    {
        var page = DataService.MAX_DATA_BYTES_PER_PAGE;
        foreach (var target in new[]
        {
            page - 1, page, page + 1, page * 2 - 1, page * 2, page * 2 + 1,
            MAX_DOCUMENT_SIZE - 1, MAX_DOCUMENT_SIZE, MAX_DOCUMENT_SIZE + 1
        })
        {
            var document = SizedDocument(target);
            context.Check(document.GetBytesCount(true) == target, "Document boundary generator was not exact.");
            var file = Path.Combine(context.DirectoryPath, $"document-{target}.db");
            var accepted = false;
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, TransactionPageLimit = 32 }))
            {
                try { db.GetCollection("rows").Insert(document); accepted = true; }
                catch (Exception error) when (CleanFailure(error)) { }
                db.Checkpoint();
            }
            context.Check(accepted == (target <= MAX_DOCUMENT_SIZE),
                $"Document boundary {target} had the wrong success result.");
            DatabaseIntegrityVerifier.Verify(context, file);
        }
    }

    private static void VerifyTransactionPageLimits(FuzzContext context)
    {
        foreach (var limit in new[] { 1, 2, 3 })
        {
            var file = Path.Combine(context.DirectoryPath, $"transaction-pages-{limit}.db");
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, TransactionPageLimit = limit }))
            {
                db.BeginTrans();
                var rows = db.GetCollection("rows");
                for (var id = 1; id <= 8; id++)
                    rows.Insert(new BsonDocument { ["_id"] = id, ["Payload"] = new byte[9000] });
                context.Check(db.Commit(), $"TransactionPageLimit={limit} did not commit.");
                db.Checkpoint();
            }
            DatabaseIntegrityVerifier.Verify(context, file);
        }
    }

    private static void VerifyCollectionIndexSlots(FuzzContext context)
    {
        var file = Path.Combine(context.DirectoryPath, "collection-index-slots.db");
        using (var db = new LiteDatabase(file))
        {
            var rows = db.GetCollection("rows");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["Value"] = 1 });
            for (var slot = 1; slot < byte.MaxValue; slot++)
                context.Check(rows.EnsureIndex("i" + slot.ToString("D3"), "Value"),
                    $"Collection rejected index slot {slot} before the byte boundary.");
            var rejected = false;
            try { rows.EnsureIndex("i255", "Value"); }
            catch (Exception error) when (CleanFailure(error)) { rejected = true; }
            context.Check(rejected, "Collection accepted a 256th index slot.");
            db.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, file);
    }

    private static void VerifyAllocationFailure(FuzzContext context)
    {
        using var baselineData = new MemoryStream();
        using var baselineLog = new MemoryStream();
        using (var db = new LiteDatabase(baselineData, logStream: baselineLog))
        {
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            db.Checkpoint();
        }
        var dataBytes = baselineData.ToArray();
        var logBytes = baselineLog.ToArray();
        var large = SizedDocument(DataService.MAX_DATA_BYTES_PER_PAGE * 3);
        large["_id"] = 2;

        long requiredLength;
        using (var probeData = Initialized(dataBytes))
        using (var probeLog = Initialized(logBytes))
        {
            using (var db = new LiteDatabase(probeData, logStream: probeLog))
            {
                db.GetCollection("rows").Insert(large);
                db.Checkpoint();
            }
            requiredLength = probeData.Length;
        }
        context.Check(requiredLength >= dataBytes.Length + PAGE_SIZE,
            "Allocation-boundary workload did not require another page.");

        using var quotaData = new CapacityStream(dataBytes, requiredLength - PAGE_SIZE);
        using var quotaLog = Initialized(logBytes);
        var failed = false;
        LiteDatabase limited = null;
        try
        {
            limited = new LiteDatabase(quotaData, logStream: quotaLog);
            limited.GetCollection("rows").Insert(large);
            limited.Checkpoint();
        }
        catch (IOException) { failed = true; }
        finally
        {
            try { limited?.Dispose(); }
            catch (IOException) { failed = true; }
        }
        context.Check(failed, "Storage succeeded with capacity exactly one page below its known requirement.");

        using var recoveredData = Initialized(quotaData.ToArray());
        using var recoveredLog = Initialized(quotaLog.ToArray());
        using (var recovered = new LiteDatabase(recoveredData, logStream: recoveredLog))
        {
            context.Check(recovered.GetCollection("rows").FindById(2) != null,
                "Recovery lost the transaction after a one-page-short checkpoint failure.");
            recovered.Checkpoint();
        }
        var file = Path.Combine(context.DirectoryPath, "allocation-one-page-short.db");
        File.WriteAllBytes(file, recoveredData.ToArray());
        DatabaseIntegrityVerifier.Verify(context, file);
    }

    private static void VerifyNesting(FuzzContext context)
    {
        // Keep the persisted round trip within the reader's supported nesting contract.
        // The outer row and Value documents add two more container levels.
        foreach (var depth in new[] { 64, 200 })
        {
            var file = Path.Combine(context.DirectoryPath, $"nesting-{depth}.db");
            var value = new BsonDocument { ["leaf"] = true };
            for (var level = 0; level < depth; level++) value = new BsonDocument { ["next"] = value };
            using (var db = new LiteDatabase(file))
            {
                try { db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["Value"] = value }); }
                catch (Exception error) when (CleanFailure(error)) { }
                db.Checkpoint();
            }
            DatabaseIntegrityVerifier.Verify(context, file);
        }
    }

    private static BsonValue StringKey(int length)
    {
        for (var characters = Math.Max(0, length - 8); characters <= length; characters++)
        {
            var value = new BsonValue(new string('k', characters));
            if (IndexNode.GetKeyLength(value, false) == length) return value;
        }
        throw new InvalidOperationException($"No string key has encoded length {length}.");
    }

    private static bool CleanFailure(Exception error) => error is LiteException or IOException or
        InvalidOperationException or ArgumentException or NotSupportedException;

    private static BsonDocument SizedDocument(int target)
    {
        var template = new BsonDocument { ["_id"] = 1, ["Payload"] = Array.Empty<byte>() };
        return new BsonDocument
        {
            ["_id"] = 1,
            ["Payload"] = new byte[target - template.GetBytesCount(true)]
        };
    }

    private static MemoryStream Initialized(byte[] bytes)
    {
        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;
        return stream;
    }

    private sealed class CapacityStream : MemoryStream
    {
        private readonly long _limit;

        internal CapacityStream(byte[] bytes, long limit)
        {
            _limit = limit;
            base.Write(bytes, 0, bytes.Length);
            Position = 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > _limit) throw new IOException("Injected capacity boundary.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > _limit) throw new IOException("Injected capacity boundary.");
            base.Write(buffer);
        }

        public override void SetLength(long value)
        {
            if (value > _limit) throw new IOException("Injected capacity boundary.");
            base.SetLength(value);
        }
    }
}
