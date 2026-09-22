using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

// A bounded volatile/durable device. Flush() does not persist bytes. Every
// durable barrier, write and truncate is eligible for a replayable crash image.
internal sealed class ChecksumCrashDevice : IDisposable
{
    internal readonly DeviceStream Data;
    internal readonly DeviceStream Log;
    private readonly Random _random;
    private int _events;
    internal bool Armed;
    internal byte[] SelectedData;
    internal byte[] SelectedLog;
    internal string SelectedEvent;
    internal int SelectedPrefix;
    internal bool SelectedDurable;
    internal int Events => _events;

    internal ChecksumCrashDevice(byte[] data, byte[] log, Random random)
    {
        _random = random;
        Data = new DeviceStream(data, this, "data");
        Log = new DeviceStream(log, this, "log");
    }

    private void Capture(DeviceStream source, string operation, byte[] write = null, long position = 0)
    {
        if (!Armed || _random.Next(++_events) != 0) return;
        var durable = _random.Next(2) == 0;
        var data = durable ? Data.Durable.ToArray() : Data.ToArray();
        var log = durable ? Log.Durable.ToArray() : Log.ToArray();
        var prefix = 0;
        if (write != null)
        {
            var boundaries = new[] { 0, 1, 8, 15, 31, 32, 511, 512, 4096, write.Length - 1, write.Length, _random.Next(write.Length + 1) };
            prefix = Math.Min(write.Length, boundaries[_random.Next(boundaries.Length)]);
            var bytes = source == Data ? data : log;
            // A zero-byte write must not extend the file.
            if (prefix > 0)
            {
                if (bytes.Length < position + prefix) Array.Resize(ref bytes, checked((int)position + prefix));
                Buffer.BlockCopy(write, 0, bytes, checked((int)position), prefix);
            }
            if (source == Data) data = bytes; else log = bytes;
        }
        SelectedData = data;
        SelectedLog = log;
        SelectedEvent = source.Name + "." + operation;
        SelectedPrefix = prefix;
        SelectedDurable = durable;
    }

    internal sealed class DeviceStream : MemoryStream, IDurableStream
    {
        private readonly ChecksumCrashDevice _device;
        internal readonly string Name;
        internal byte[] Durable;

        internal DeviceStream(byte[] initial, ChecksumCrashDevice device, string name)
        {
            _device = device;
            Name = name;
            base.Write(initial, 0, initial.Length);
            Position = 0;
            Durable = initial.ToArray();
        }

        public void FlushToDisk()
        {
            _device.Capture(this, "before-sync");
            Durable = ToArray();
            _device.Capture(this, "after-sync");
        }

        public override void Flush() => _device.Capture(this, "flush");

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;
            _device.Capture(this, "torn-write", buffer.AsSpan(offset, count).ToArray(), Position);
            base.Write(buffer, offset, count);
            _device.Capture(this, "after-write");
        }

        public override void SetLength(long value)
        {
            _device.Capture(this, "before-truncate");
            base.SetLength(value);
            _device.Capture(this, "after-truncate");
        }
    }

    public void Dispose() { Data.Dispose(); Log.Dispose(); }
}
