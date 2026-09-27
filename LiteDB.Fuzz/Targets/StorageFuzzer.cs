namespace LiteDB.Fuzz.Targets;

internal sealed class StorageFuzzer : IFuzzTarget
{
    public string Name => "storage";
    public string Description => "FileStorage upload, streaming chunks, overwrite, metadata, delete, and rollback model.";

    public Task RunAsync(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "storage.db"));
        var model = new SortedDictionary<string, StoredFile>(StringComparer.Ordinal);
        var connection = new ConnectionString { Filename = file, TransactionPageLimit = 4, DurableCommits = true };
        var db = new LiteDatabase(connection);
        var reopens = 0;
        var interruptedTransactions = 0;
        var integrityChecks = 0;
        var atomicUploadProbes = 0;
        var injectedUploadFailures = 0;
        var failureBaseline = CreateFailureBaseline();
        try
        {
            while (context.Next())
            {
                var id = "file-" + context.Random.Next(12);
                var operation = context.Random.Next(9);
                if (operation <= 1)
                {
                    var bytes = Bytes(context.Random, context.Random.Next(0, LiteFileStream<string>.MAX_CHUNK_SIZE * 2 + 137));
                    var metadata = Metadata(context.Random);
                    using var source = new MemoryStream(bytes);
                    db.FileStorage.Upload(id, id + ".bin", source, metadata);
                    model[id] = new StoredFile(id + ".bin", bytes, Clone(metadata));
                    context.Trace("upload", new { id, bytes = bytes.Length });
                }
                else if (operation == 2)
                {
                    var bytes = Bytes(context.Random, context.Random.Next(0, LiteFileStream<string>.MAX_CHUNK_SIZE + 51));
                    using (var writer = db.FileStorage.OpenWrite(id, id + ".stream", Metadata(context.Random)))
                    {
                        var position = 0;
                        while (position < bytes.Length)
                        {
                            var count = Math.Min(bytes.Length - position, context.Random.Next(1, 4097));
                            writer.Write(bytes, position, count);
                            if (context.Random.Next(5) == 0) writer.Flush();
                            position += count;
                        }
                    }
                    var info = db.FileStorage.FindById(id);
                    model[id] = new StoredFile(info.Filename, bytes, Clone(info.Metadata));
                    context.Trace("stream-write", new { id, bytes = bytes.Length });
                }
                else if (operation == 3 && model.ContainsKey(id))
                {
                    var metadata = Metadata(context.Random);
                    context.Check(db.FileStorage.SetMetadata(id, metadata), "SetMetadata returned false for a modeled file.");
                    model[id] = model[id] with { Metadata = Clone(metadata) };
                    context.Trace("metadata", id);
                }
                else if (operation == 4)
                {
                    var expected = model.Remove(id);
                    context.Check(db.FileStorage.Delete(id) == expected, "FileStorage.Delete disagreed with the model.");
                    context.Trace("delete", id);
                }
                else if (operation == 5)
                {
                    RollbackMutation(context, db, model, id);
                }
                else if (operation == 6)
                {
                    db.BeginTrans();
                    using (var source = new MemoryStream(Bytes(context.Random, context.Random.Next(1, 8000))))
                        db.FileStorage.Upload("interrupted", "interrupted.bin", source);
                    db.Dispose();
                    db = new LiteDatabase(connection);
                    interruptedTransactions++;
                    context.Trace("dispose-uncommitted");
                }
                else if (operation == 7)
                {
                    db.Checkpoint();
                    db.Dispose();
                    db = new LiteDatabase(connection);
                    reopens++;
                    context.Trace("reopen");
                }
                else if (model.Count != 0)
                {
                    var existing = model.Keys.ElementAt(context.Random.Next(model.Count));
                    using var read = db.FileStorage.OpenRead(existing);
                    if (read.Length != 0) read.Seek(context.Random.NextInt64(read.Length), SeekOrigin.Begin);
                    _ = read.ReadByte();
                    context.Trace("seek-read", existing);
                }
                Validate(context, db, model);
                context.Check(db.FileStorage.FindById("interrupted") == null,
                    "An uncommitted staged FileStorage publication survived reopen.");
                if (context.Steps % 53 == 0)
                {
                    db.Checkpoint();
                    DatabaseIntegrityVerifier.Verify(context, file);
                    integrityChecks++;
                }
                if (context.Steps % 17 == 0)
                {
                    ProbeInterruptedUpload(context, failureBaseline, ref injectedUploadFailures, ref integrityChecks);
                    atomicUploadProbes++;
                }
            }
            db.Checkpoint();
            DatabaseIntegrityVerifier.Verify(context, file);
            integrityChecks++;
            if (context.Steps >= 100)
                context.Check(reopens > 0 && interruptedTransactions > 0 && atomicUploadProbes > 0 &&
                    injectedUploadFailures > 0 && integrityChecks > 1,
                    "Storage campaign missed reopen, staged-publication failure, or integrity paths.");
            context.Metrics["files"] = model.Count;
            context.Metrics["bytes"] = model.Values.Sum(value => value.Bytes.Length);
            context.Metrics["reopens"] = reopens;
            context.Metrics["interruptedTransactions"] = interruptedTransactions;
            context.Metrics["atomicUploadProbes"] = atomicUploadProbes;
            context.Metrics["injectedUploadFailures"] = injectedUploadFailures;
            context.Metrics["integrityChecks"] = integrityChecks;
        }
        finally { db.Dispose(); }
        return Task.CompletedTask;
    }

    private static void RollbackMutation(FuzzContext context, LiteDatabase db,
        SortedDictionary<string, StoredFile> model, string id)
    {
        db.BeginTrans();
        try
        {
            if (model.ContainsKey(id)) db.FileStorage.Delete(id);
            else
            {
                var bytes = Bytes(context.Random, context.Random.Next(1, 4000));
                using var source = new MemoryStream(bytes);
                db.FileStorage.Upload(id, "rolled-back", source);
            }
            db.Rollback();
        }
        catch
        {
            db.Rollback();
            throw;
        }
        context.Trace("rollback", id);
    }

    private static (byte[] Data, byte[] Log, byte[] Old) CreateFailureBaseline()
    {
        using var data = new MemoryStream();
        using var log = new MemoryStream();
        var old = Enumerable.Range(0, 9000).Select(index => (byte)(index * 17)).ToArray();
        using (var db = new LiteDatabase(data, logStream: log))
        using (var source = new MemoryStream(old))
        {
            db.FileStorage.Upload("atomic", "old.bin", source, new BsonDocument { ["version"] = "old" });
            db.Checkpoint();
        }
        return (data.ToArray(), log.ToArray(), old);
    }

    private static void ProbeInterruptedUpload(FuzzContext context, (byte[] Data, byte[] Log, byte[] Old) baseline,
        ref int injectedFailures, ref int integrityChecks)
    {
        using var data = Initialized(baseline.Data);
        using var log = Initialized(baseline.Log);
        var engine = new LiteDB.Engine.LiteEngine(new LiteDB.Engine.EngineSettings
        {
            DataStream = data, LogStream = log, TransactionPageLimit = 4, DurableCommits = true
        });
        var eventCount = 0;
        var fired = false;
        var failAt = (context.Steps / 17 - 1) % 48 + 1;
        engine.SimulateDiskWriteFail = _ =>
        {
            if (++eventCount != failAt) return;
            fired = true;
            throw new IOException($"Injected FileStorage page-write failure {failAt}.");
        };
        var next = Bytes(context.Random, LiteFileStream<string>.MAX_CHUNK_SIZE * 2 + 97);
        var acknowledged = false;
        LiteDatabase db = null;
        try
        {
            db = new LiteDatabase(engine);
            using var source = new MemoryStream(next);
            db.FileStorage.Upload("atomic", "new.bin", source, new BsonDocument { ["version"] = "new" });
            acknowledged = true;
        }
        catch when (fired) { injectedFailures++; }
        finally
        {
            engine.SimulateDiskWriteFail = null;
            try { db?.Dispose(); }
            catch when (fired) { }
        }

        using var recoveredData = Initialized(data.ToArray());
        using var recoveredLog = Initialized(log.ToArray());
        using var recovered = new LiteDatabase(recoveredData, logStream: recoveredLog);
        using var output = new MemoryStream();
        var info = recovered.FileStorage.Download("atomic", output);
        var bytes = output.ToArray();
        var isOld = bytes.SequenceEqual(baseline.Old) && info.Filename == "old.bin" && info.Metadata["version"] == "old";
        var isNew = bytes.SequenceEqual(next) && info.Filename == "new.bin" && info.Metadata["version"] == "new";
        context.Check(isOld || isNew, "Interrupted FileStorage upload exposed mixed metadata or partial chunks.");
        context.Check(!acknowledged || isNew, "Acknowledged FileStorage upload was lost during recovery.");
        if (context.Steps % 85 == 0)
        {
            recovered.Checkpoint();
            var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "storage-recovered.db"));
            File.WriteAllBytes(file, recoveredData.ToArray());
            DatabaseIntegrityVerifier.Verify(context, file);
            integrityChecks++;
        }
        context.Trace("atomic-upload-failure", new { failAt, eventCount, fired, acknowledged, recovered = isNew ? "new" : "old" });
    }

    private static void Validate(FuzzContext context, LiteDatabase db, SortedDictionary<string, StoredFile> model)
    {
        var infos = db.FileStorage.FindAll().OrderBy(info => info.Id, StringComparer.Ordinal).ToArray();
        context.Check(infos.Length == model.Count, "FileStorage file count mismatch.");
        foreach (var pair in model)
        {
            var info = db.FileStorage.FindById(pair.Key);
            context.Check(info != null && info.Filename == pair.Value.Name && info.Length == pair.Value.Bytes.Length,
                $"FileStorage metadata mismatch for {pair.Key}.");
            context.Check(BsonSerializer.Serialize(info.Metadata).SequenceEqual(BsonSerializer.Serialize(pair.Value.Metadata)),
                $"FileStorage custom metadata mismatch for {pair.Key}.");
            using var output = new MemoryStream();
            db.FileStorage.Download(pair.Key, output);
            context.Check(output.ToArray().SequenceEqual(pair.Value.Bytes), $"FileStorage bytes mismatch for {pair.Key}.");
        }
    }

    private static byte[] Bytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }

    private static MemoryStream Initialized(byte[] bytes)
    {
        var stream = new MemoryStream();
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 0;
        return stream;
    }

    private static BsonDocument Metadata(Random random) => new()
    {
        ["version"] = random.Next(), ["label"] = "m" + random.Next(100),
        ["nested"] = new BsonDocument { ["flag"] = random.Next(2) == 0 }
    };

    private static BsonDocument Clone(BsonDocument document) => BsonSerializer.Deserialize(BsonSerializer.Serialize(document));
    private sealed record StoredFile(string Name, byte[] Bytes, BsonDocument Metadata);
}
