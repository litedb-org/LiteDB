namespace LiteDB.Fuzz.Targets;

internal sealed class ChaosThrowingStream : MemoryStream
{
    private int _reads = 3;

    internal ChaosThrowingStream(byte[] bytes) : base(bytes) { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (--_reads == 0) throw new IOException("Injected chaos source failure.");
        return base.Read(buffer, offset, Math.Min(count, 1024));
    }

    public override int Read(Span<byte> buffer)
    {
        if (--_reads == 0) throw new IOException("Injected chaos source failure.");
        return base.Read(buffer[..Math.Min(buffer.Length, 1024)]);
    }
}
