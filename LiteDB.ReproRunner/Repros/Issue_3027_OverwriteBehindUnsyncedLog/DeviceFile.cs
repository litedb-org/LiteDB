namespace Issue_3027_OverwriteBehindUnsyncedLog;

/// <summary>
/// A file on a modeled device. Its device image changes only at a successful sync (Flush(true), which both
/// engine versions call for a caller's FileStream subclass): the writes and SetLength calls since then are
/// pending, in order. While <see cref="CannotSync"/> is set, a sync answers EINVAL, which both classify as
/// "cannot sync" (#2242), and the writes stay pending.
/// </summary>
internal sealed class DeviceFile : FileStream
{
    private const int EINVAL = 22;
    private readonly object _gate = new();
    private readonly List<(long Position, byte[]? Bytes, long Length, long Number)> _pending = new();
    private byte[] _device;
    private long _operations;
    internal volatile bool CannotSync;
    internal int Refused;
    internal Action? BeforeSync;

    internal DeviceFile(string path)
        : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1)
    {
        _device = Array.Empty<byte>();
    }

    /// <summary>The bytes on the device: as of the last successful sync.</summary>
    internal byte[] Device { get { lock (_gate) return _device; } }

    /// <summary>The bytes in the cache: what a process crash leaves.</summary>
    internal byte[] Live
    {
        get
        {
            using var reader = new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[reader.Length];
            reader.ReadExactly(bytes);
            return bytes;
        }
    }

    internal long Operations { get { lock (_gate) return _operations; } }
    internal int Pending { get { lock (_gate) return _pending.Count; } }
    internal int PendingSince(long operation) { lock (_gate) return _pending.Count(x => x.Number >= operation); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var at = Position;
        base.Write(buffer, offset, count);
        lock (_gate) _pending.Add((at, buffer.AsSpan(offset, count).ToArray(), -1, _operations++));
    }

    public override void SetLength(long value)
    {
        base.SetLength(value);
        lock (_gate) _pending.Add((0, null, value, _operations++));
    }

    public override void Flush(bool flushToDisk)
    {
        base.Flush(false);
        if (!flushToDisk) return;
        if (CannotSync)
        {
            Interlocked.Increment(ref Refused);
            throw new IOException($"fsync of '{Name}' failed: Invalid argument", EINVAL);
        }
        BeforeSync?.Invoke();
        lock (_gate)
        {
            _device = Apply(_pending.Count, torn: false);
            _pending.Clear();
        }
    }

    /// <summary>The pending writes, and how many of them overwrite bytes a device image of that length holds.</summary>
    internal (int Writes, int Overwrites) PendingWrites(long deviceLength)
    {
        lock (_gate)
        {
            var writes = _pending.Where(x => x.Bytes != null).ToList();
            return (writes.Count, writes.Count(x => x.Position < deviceLength));
        }
    }

    /// <summary>
    /// Every data image a power loss may leave now: after the first k pending operations reached the device,
    /// the k-th (a write) whole or torn in half (its first half, sector aligned, over what the device held).
    /// </summary>
    internal List<(string Name, byte[] Image)> PowerLossImages()
    {
        lock (_gate)
        {
            var images = new List<(string, byte[])>();
            var writes = 0;
            var total = _pending.Count(x => x.Bytes != null);
            for (var k = 1; k <= _pending.Count; k++)
            {
                var (position, bytes, _, _) = _pending[k - 1];
                if (bytes == null) continue;
                writes++;
                var at = $"{bytes.Length} bytes at {position}";
                if (bytes.Length > 512) images.Add(($"after write {writes} of {total} torn ({at})", Apply(k, torn: true)));
                images.Add(($"after write {writes} of {total} ({at})", Apply(k, torn: false)));
            }
            return images;
        }
    }

    // The device image after the first count pending operations reached it, the last write torn when asked.
    private byte[] Apply(int count, bool torn)
    {
        var image = (byte[])_device.Clone();
        for (var i = 0; i < count; i++)
        {
            var (position, bytes, length, _) = _pending[i];
            if (bytes == null)
            {
                Array.Resize(ref image, (int)length);
                continue;
            }
            var written = torn && i == count - 1 ? bytes.Length / 2 / 512 * 512 : bytes.Length;
            if (position + bytes.Length > image.Length) Array.Resize(ref image, (int)(position + bytes.Length));
            Buffer.BlockCopy(bytes, 0, image, (int)position, written);
        }
        return image;
    }
}
