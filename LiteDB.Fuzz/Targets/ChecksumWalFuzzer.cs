using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class ChecksumWalFuzzer : IFuzzTarget
{
    public string Name => "checksum-wal";
    public string Description => "Frame mutations and CRC-valid invalid confirmations must recover exactly the preceding committed prefix.";

    public Task RunAsync(FuzzContext context)
    {
        while (context.Next())
        {
            using var fixture = new ChecksumFixture(context);
            byte[] stale;
            using (var previous = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password))
            {
                previous.CheckpointSize = 0;
                // Same logical contents, but an earlier WAL generation.
                previous.GetCollection("rows").Update(ChecksumFixture.Clone(fixture.Rows.Values.First()));
                stale = ChecksumFixture.WalBytes(fixture.Log, fixture.Password);
                previous.Checkpoint();
            }
            var models = new List<SortedDictionary<int, BsonDocument>> { new(fixture.Rows) };
            var ends = new List<int> { 0 };
            using (var db = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password))
            {
                db.CheckpointSize = 0;
                for (var transaction = 0; transaction < 4; transaction++)
                {
                    db.BeginTrans();
                    var model = new SortedDictionary<int, BsonDocument>(models[^1]);
                    for (var operation = 0; operation < 5; operation++)
                    {
                        var id = context.Random.Next(1, 25);
                        var row = ChecksumFixture.Document(context.Random, id);
                        db.GetCollection("rows").Upsert(ChecksumFixture.Clone(row));
                        model[id] = row;
                    }
                    db.Commit();
                    models.Add(model);
                    ends.Add(ChecksumFixture.WalBytes(fixture.Log, fixture.Password).Length);
                }
            }
            var bytes = ChecksumFixture.WalBytes(fixture.Log, fixture.Password);
            var transactionIndex = context.Random.Next(4);
            var kind = (context.Steps - 1) % 14;
            var frameSize = WalChecksum.FrameSize;
            var start = ends[transactionIndex];
            var end = ends[transactionIndex + 1];
            var frame = start + context.Random.Next((end - start) / frameSize) * frameSize;
            if (kind is >= 4 and <= 7) frame = end - frameSize;
            var location = context.Random.Next(frameSize);
            switch (kind)
            {
                case 0: bytes[frame + location] ^= (byte)(1 << context.Random.Next(8)); break;
                case 1: Array.Resize(ref bytes, frame + context.Random.Next(frameSize)); break;
                case 2:
                    Buffer.BlockCopy(bytes, frame + frameSize, bytes, frame, bytes.Length - frame - frameSize);
                    Array.Resize(ref bytes, bytes.Length - frameSize);
                    break;
                case 3: Array.Clear(bytes, frame, frameSize); break;
                case 4: bytes[frame + 8192 + 32] ^= 1; Stamp(bytes, frame); break;
                case 5: bytes[frame + 8192 + 36 + context.Random.Next(8)] ^= 1; Stamp(bytes, frame); break;
                case 6: bytes[frame + 8192 + 44] ^= 1; Stamp(bytes, frame); break;
                case 7:
                    bytes[frame + 18] = 0; // Lose confirmation; a later commit may not bridge this sequence gap.
                    Array.Clear(bytes, frame + 8192 + 44, 8);
                    Stamp(bytes, frame);
                    break;
                case 8: bytes[frame + 8192 + 8 + context.Random.Next(16)] ^= 1; Stamp(bytes, frame); break;
                case 10: Buffer.BlockCopy(stale, 0, bytes, frame, frameSize); break;
                case 11:
                    BitConverter.GetBytes((long)frame / frameSize * 8192 + 8192).CopyTo(bytes, frame + 8192 + 24);
                    Stamp(bytes, frame);
                    break;
                case 12:
                    // Duplicate a frame at another position without deleting bytes.
                    var other = frame == start ? frame + frameSize : frame - frameSize;
                    Buffer.BlockCopy(bytes, other, bytes, frame, frameSize);
                    break;
                case 13:
                    var neighbor = frame == start ? frame + frameSize : frame - frameSize;
                    var saved = bytes.AsSpan(frame, frameSize).ToArray();
                    Buffer.BlockCopy(bytes, neighbor, bytes, frame, frameSize);
                    Buffer.BlockCopy(saved, 0, bytes, neighbor, frameSize);
                    break;
                case 9: bytes[frame + 31] = context.Random.Next(2) == 0 ? (byte)0 : (byte)255; Stamp(bytes, frame); break;
            }
            // Truncation acts on ciphertext too; do not try to encrypt a partial AES block.
            if (kind == 1) fixture.Log.SetLength(bytes.Length + (fixture.Password == null ? 0 : 8192));
            else ChecksumFixture.Replace(fixture.Log, bytes, fixture.Password);
            var dataBytes = fixture.Data.ToArray();
            var logBytes = fixture.Log.ToArray();
            ChecksumFixture.Save(context, dataBytes, logBytes);
            foreach (var readOnly in new[] { true, false })
            {
                using var data = ChecksumFixture.Copy(dataBytes);
                using var log = ChecksumFixture.Copy(logBytes);
                using (var recovered = ChecksumFixture.Open(data, log, fixture.Password, readOnly))
                {
                    ChecksumFixture.Verify(context, recovered, models[transactionIndex], fixture.Cold);
                    if (!readOnly) recovered.Checkpoint();
                }
                if (readOnly) context.Check(data.ToArray().SequenceEqual(dataBytes) && log.ToArray().SequenceEqual(logBytes),
                    "Read-only WAL recovery modified a damaged image.");
                else
                {
                    ChecksumFixture.Audit(context, data, fixture.Password);
                    using var reopened = ChecksumFixture.Open(data, log, fixture.Password, true);
                    ChecksumFixture.Verify(context, reopened, models[transactionIndex], fixture.Cold);
                }
            }
            context.Trace("wal-corruption", new { kind, transactionIndex, frame, location, encrypted = fixture.Password != null });
            context.ObserveNovelty("wal-prefix", kind, transactionIndex, fixture.Password != null);
        }
        context.Metrics["recoveredPrefixes"] = context.Steps * 3;
        return Task.CompletedTask;
    }

    private static void Stamp(byte[] bytes, int frame) => BitConverter.GetBytes(
        ChecksumFixture.Crc(bytes, frame, WalChecksum.FrameSize, 8196, 4)).CopyTo(bytes, frame + 8196);
}
