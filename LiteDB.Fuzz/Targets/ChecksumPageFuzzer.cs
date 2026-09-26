using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class ChecksumPageFuzzer : IFuzzTarget
{
    public string Name => "checksum-page";
    public string Description => "Random page bits, CRC-valid markers/identities/coverage and legacy-boundary rejection.";

    public Task RunAsync(FuzzContext context)
    {
        while (context.Next())
        {
            using var fixture = new ChecksumFixture(context);
            var mixed = context.Random.Next(2) == 0;
            if (mixed)
            {
                fixture.MakeLegacy((byte)(8 + context.Random.Next(2)));
                using var migrated = ChecksumFixture.Open(fixture.Data, fixture.Log, fixture.Password);
            }
            var pristine = fixture.Data.ToArray();
            var bytes = ChecksumFixture.Plain(fixture.Data, fixture.Password);
            var kind = (context.Steps - 1) % 7;
            var id = kind >= 4 ? 0 : 1; // Collection page is always read by a collection scan.
            var offset = id * 8192;
            // In Mixed mode convert this one page first, so a payload bit flip
            // must be detected even though its ID lies inside the legacy range.
            bytes[offset + 31] = 0xA5;
            Stamp(bytes, offset);
            var position = context.Random.Next(8192);
            switch (kind)
            {
                case 0: bytes[offset + position] ^= (byte)(1 << context.Random.Next(8)); break;
                case 1:
                    byte marker;
                    do { marker = (byte)context.Random.Next(1, 256); } while (marker == 0xA5);
                    bytes[offset + 31] = marker;
                    Stamp(bytes, offset);
                    break;
                case 2:
                    bytes[offset + 31] = (byte)(0xA5 ^ (1 << context.Random.Next(8)));
                    Stamp(bytes, offset);
                    break;
                case 3:
                    BitConverter.GetBytes((uint)context.Random.Next(2, 10000)).CopyTo(bytes, offset);
                    Stamp(bytes, offset);
                    break;
                case 4:
                    do { bytes[160] = (byte)context.Random.Next(256); } while (bytes[160] is 0x5A or 0xA5);
                    Stamp(bytes, 0);
                    break;
                case 5:
                    bytes[160] = 0xA5;
                    BitConverter.GetBytes(BitConverter.ToUInt32(bytes, HeaderPage.P_LAST_PAGE_ID) + 1u).CopyTo(bytes, 161);
                    Stamp(bytes, 0);
                    break;
                case 6: bytes[31] = 0; Stamp(bytes, 0); break;
            }
            ChecksumFixture.Replace(fixture.Data, bytes, fixture.Password);
            var damaged = fixture.Data.ToArray();
            var log = fixture.Log.ToArray();
            ChecksumFixture.Save(context, damaged, log);
            foreach (var readOnly in new[] { true, false })
            {
                using var dataCopy = ChecksumFixture.Copy(damaged);
                using var logCopy = ChecksumFixture.Copy(log);
                var rejected = false;
                try
                {
                    using var db = ChecksumFixture.Open(dataCopy, logCopy, fixture.Password, readOnly);
                    db.GetCollection("cold").FindAll().ToArray();
                    db.GetCollection("rows").FindAll().ToArray();
                }
                catch (LiteException error) when (error.ErrorCode == LiteException.CHECKSUM_MISMATCH) { rejected = true; }
                context.Check(rejected, "Damaged checked page was accepted or failed through the wrong contract.");
                if (readOnly) context.Check(dataCopy.ToArray().SequenceEqual(damaged) && logCopy.ToArray().SequenceEqual(log),
                    "Read-only corruption probe changed physical bytes.");
            }
            using var original = ChecksumFixture.Copy(pristine);
            using var originalLog = ChecksumFixture.Copy(log);
            using var healthy = ChecksumFixture.Open(original, originalLog, fixture.Password, true);
            ChecksumFixture.Verify(context, healthy, fixture.Rows, fixture.Cold);
            CheckPolicyBoundary(context);
            context.Trace("page-corruption", new { kind, mixed, encrypted = fixture.Password != null, position });
            context.ObserveNovelty("page-rejection", kind, mixed, fixture.Password != null);
        }
        context.Metrics["rejectedProbes"] = context.Steps * 2;
        return Task.CompletedTask;
    }

    private static void CheckPolicyBoundary(FuzzContext context)
    {
        var random = context.Random;
        var boundary = (uint)random.Next(1, 10000);
        var mixed = random.Next(2) == 0;
        var header = new byte[8192];
        header[160] = mixed ? (byte)0xA5 : (byte)0x5A;
        BitConverter.GetBytes(boundary + 10).CopyTo(header, HeaderPage.P_LAST_PAGE_ID);
        BitConverter.GetBytes(mixed ? boundary : 0u).CopyTo(header, 161);
        var policy = new DataChecksumPolicy();
        policy.Load(new BufferSlice(header, 0, header.Length));
        foreach (var id in new[] { 0u, boundary, boundary + 1, boundary + 10 })
        {
            foreach (var marker in new byte[] { 0, 0xA5, 0xFF, (byte)random.Next(256) })
            {
                var page = new byte[8192];
                // Record a small seed pattern, not 8192 random words per probe.
                var pattern = new byte[32];
                random.NextBytes(pattern);
                for (var i = 0; i < page.Length; i++) page[i] = (byte)(pattern[i % 32] ^ (i / 32));
                var wrongIdentity = random.Next(4) == 0;
                BitConverter.GetBytes(wrongIdentity ? id + 1 : id).CopyTo(page, 0);
                page[31] = marker;
                Stamp(page, 0);
                var corrupt = random.Next(2) == 0;
                if (corrupt) page[random.Next(32, 8192)] ^= 1;
                var expected = !wrongIdentity && (marker == 0 && mixed && id > 0 && id <= boundary ||
                    marker == 0xA5 && !corrupt);
                var accepted = true;
                try { policy.Validate(new BufferSlice(page, 0, page.Length), (long)id * 8192); }
                catch (LiteException error) when (error.ErrorCode == LiteException.CHECKSUM_MISMATCH) { accepted = false; }
                context.Check(accepted == expected, "Page permission differs from the independent coverage/boundary model.");
            }
        }
    }

    private static void Stamp(byte[] bytes, int offset) =>
        BitConverter.GetBytes(ChecksumFixture.Crc(bytes, offset, 8192, 14, 4)).CopyTo(bytes, offset + 14);
}
