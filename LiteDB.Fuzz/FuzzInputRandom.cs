using System.Security.Cryptography;

namespace LiteDB.Fuzz;

/// <summary>
/// Versioned byte-input adapter. Generated runs persist every random word, while
/// replay runs consume those words instead of regenerating them from a seed.
/// This keeps old cases stable when generators change and gives mutational
/// fuzzers a simple binary input format.
/// </summary>
internal sealed class FuzzInputRandom : Random, IDisposable
{
    private const uint NonZeroSeed = 0x6d2b79f5;
    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    private uint _state;

    internal FuzzInputRandom(int seed, string outputPath, string replayPath)
    {
        OutputPath = outputPath;
        _state = unchecked((uint)seed);
        if (_state == 0) _state = NonZeroSeed;
        if (replayPath == null)
        {
            _stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _writer = new BinaryWriter(_stream);
        }
        else
        {
            if (!string.Equals(Path.GetFullPath(replayPath), Path.GetFullPath(outputPath), StringComparison.Ordinal))
                File.Copy(replayPath, outputPath, true);
            _stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _reader = new BinaryReader(_stream);
            ReplayPath = outputPath;
        }
    }

    internal string OutputPath { get; }
    internal string ReplayPath { get; }
    internal long Position => _stream.Position;

    internal void Flush() => _writer?.Flush();

    internal string Hash()
    {
        _writer?.Flush();
        var path = ReplayPath ?? OutputPath;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public override int Next() => (int)(NextUInt32() % int.MaxValue);

    public override int Next(int maxValue)
    {
        if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        return maxValue == 0 ? 0 : (int)(NextUInt32() % (uint)maxValue);
    }

    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
        var range = (ulong)((long)maxValue - minValue);
        return range == 0 ? minValue : (int)(minValue + (long)(NextUInt32() % range));
    }

    public override void NextBytes(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextUInt32();
    }

    public override void NextBytes(Span<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)NextUInt32();
    }

    public override long NextInt64() => (long)(NextUInt64() & long.MaxValue);

    public override long NextInt64(long maxValue)
    {
        if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        return maxValue == 0 ? 0 : (long)(NextUInt64() % (ulong)maxValue);
    }

    public override long NextInt64(long minValue, long maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue));
        var range = (ulong)(maxValue - minValue);
        return range == 0 ? minValue : minValue + (long)(NextUInt64() % range);
    }

    protected override double Sample() => NextUInt32() / ((double)uint.MaxValue + 1);

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _stream.Dispose();
    }

    private uint NextUInt32()
    {
        if (_reader != null)
        {
            if (_stream.Position + sizeof(uint) > _stream.Length)
                throw new InvalidDataException("Recorded fuzz input was exhausted; the generator's input contract changed.");
            return _reader.ReadUInt32();
        }

        var value = _state;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        _state = value;
        _writer.Write(value);
        return value;
    }

    private ulong NextUInt64() => ((ulong)NextUInt32() << 32) | NextUInt32();
}
