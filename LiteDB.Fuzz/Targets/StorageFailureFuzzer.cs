namespace LiteDB.Fuzz.Targets;

internal sealed class StorageFailureFuzzer : IFuzzTarget
{
    public string Name => "storage-failure";
    public string Description => "User source/destination failures, old-reader overlap, random seeks, and typed FileStorage IDs.";

    public Task RunAsync(FuzzContext context)
    {
        using var db = new LiteDatabase(new MemoryStream());
        var storage = db.GetStorage<int>("user_files", "user_chunks");
        var old = Bytes(context.Random, LiteFileStream<int>.MAX_CHUNK_SIZE * 2 + 333);
        using (var source = new MemoryStream(old)) storage.Upload(7, "old.bin", source);
        var probes = 0;
        while (context.Next())
        {
            var next = Bytes(context.Random, LiteFileStream<int>.MAX_CHUNK_SIZE * 2 + 777);
            Exception uploadFailure = null;
            try { using var source = new ThrowingReadStream(next, 2 + context.Steps % 5); storage.Upload(7, "new.bin", source); }
            catch (Exception error) { uploadFailure = error; }
            context.Check(uploadFailure is IOException, "Throwing FileStorage source did not preserve its IOException.");
            context.Check(Read(storage, 7).SequenceEqual(old), "Failed source upload replaced or mixed the old file.");

            Exception downloadFailure = null;
            try { using var destination = new ThrowingWriteStream(1000 + context.Steps % 5000); storage.Download(7, destination); }
            catch (Exception error) { downloadFailure = error; }
            context.Check(downloadFailure is IOException, "Throwing FileStorage destination did not preserve its IOException.");
            context.Check(Read(storage, 7).SequenceEqual(old), "Failed destination download changed stored bytes.");

            using (var reader = storage.OpenRead(7))
            {
                var prefix = new byte[Math.Min(257, old.Length)];
                context.Check(reader.Read(prefix, 0, prefix.Length) == prefix.Length, "Old storage reader returned a short prefix.");
                using (var source = new MemoryStream(next)) storage.Upload(7, "new.bin", source);
                var remainder = new byte[reader.Length - reader.Position];
                var offset = 0;
                Exception overlapFailure = null;
                try
                {
                    while (offset < remainder.Length)
                    {
                        var read = reader.Read(remainder, offset, remainder.Length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                }
                catch (Exception error) { overlapFailure = error; }
                context.Check(overlapFailure is LiteException ||
                    prefix.Concat(remainder.Take(offset)).SequenceEqual(old),
                    "Overwriting FileStorage exposed mixed old and replacement chunks.");
            }
            old = next;
            context.Check(Read(storage, 7).SequenceEqual(old), "Replacement bytes changed after overlapping reader.");
            using (var reader = storage.OpenRead(7))
            {
                for (var i = 0; i < 12; i++)
                {
                    var position = context.Random.NextInt64(old.Length + 1L);
                    context.Check(reader.Seek(position, SeekOrigin.Begin) == position, "FileStorage seek returned wrong position.");
                    var value = reader.ReadByte();
                    context.Check(value == (position == old.Length ? -1 : old[position]), "FileStorage random seek read wrong byte.");
                }
            }
            probes++;
            context.ObserveNovelty("storage-user-failure", context.Steps % 5, old.Length / 4096);
        }
        context.Metrics["userStreamFailureProbes"] = probes;
        return Task.CompletedTask;
    }

    private static byte[] Read(ILiteStorage<int> storage, int id)
    {
        using var output = new MemoryStream();
        storage.Download(id, output);
        return output.ToArray();
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        private int _reads;
        internal ThrowingReadStream(byte[] bytes, int reads) : base(bytes) { _reads = reads; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (--_reads == 0) throw new IOException("Injected source read failure.");
            return base.Read(buffer, offset, Math.Min(count, 1024));
        }
        public override int Read(Span<byte> buffer)
        {
            if (--_reads == 0) throw new IOException("Injected source read failure.");
            return base.Read(buffer[..Math.Min(buffer.Length, 1024)]);
        }
    }

    private sealed class ThrowingWriteStream : MemoryStream
    {
        private readonly int _limit;
        internal ThrowingWriteStream(int limit) => _limit = limit;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > _limit) throw new IOException("Injected destination write failure.");
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > _limit) throw new IOException("Injected destination write failure.");
            base.Write(buffer);
        }
    }
}
