using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using LiteDB.Engine;

namespace LiteDB.Tests.Engine
{
    // Physical byte streams below encryption. Successful durable barriers persist
    // the whole stream; ordinary Flush does not. Power-safe overwrite preserves
    // bytes outside the current write, whose first 15 bytes may reach storage.
    internal sealed class IndexMigrationCrashDevice : IDisposable
    {
        internal readonly DeviceStream Data;
        internal readonly DeviceStream Log;
        internal readonly List<Image> Images = new List<Image>();
        internal string Stage = "migration";
        internal bool Armed = true;
        internal bool FirstEventOnly;
        internal readonly HashSet<string> Events = new HashSet<string>();
        internal int PhysicalBoundaries;
        private readonly HashSet<string> _images = new HashSet<string>();

        internal IndexMigrationCrashDevice(byte[] data, byte[] log)
        {
            Data = new DeviceStream(data, this, "data");
            Log = new DeviceStream(log, this, "log");
        }

        private void Capture(DeviceStream source, string operation, byte[] write = null, int offset = 0, int count = 0)
        {
            if (!Armed) return;
            PhysicalBoundaries++;
            var name = Stage + ":" + source.Name + "." + operation;
            var first = Events.Add(name);
            if (FirstEventOnly && !first) return;
            var data = (byte[])Data.Durable.Clone();
            var log = (byte[])Log.Durable.Clone();
            if (write != null)
            {
                var bytes = source == Data ? data : log;
                var prefix = Math.Min(15, count);
                var end = checked((int)source.Position + prefix);
                if (bytes.Length < end) Array.Resize(ref bytes, end);
                Buffer.BlockCopy(write, offset, bytes, (int)source.Position, prefix);
                if (source == Data) data = bytes; else log = bytes;
            }
            using var sha = SHA256.Create();
            var signature = Convert.ToBase64String(sha.ComputeHash(data)) + ":" + Convert.ToBase64String(sha.ComputeHash(log));
            if (_images.Add(signature)) Images.Add(new Image { Data = data, Log = log, Event = name });
        }

        internal sealed class Image
        {
            internal byte[] Data;
            internal byte[] Log;
            internal string Event;
        }

        internal sealed class DeviceStream : MemoryStream, IDurableStream
        {
            private readonly IndexMigrationCrashDevice _device;
            internal readonly string Name;
            internal byte[] Durable;

            internal DeviceStream(byte[] bytes, IndexMigrationCrashDevice device, string name)
            {
                _device = device;
                Name = name;
                base.Write(bytes, 0, bytes.Length);
                Position = 0;
                Durable = (byte[])bytes.Clone();
            }

            public void FlushToDisk()
            {
                _device.Capture(this, "before-sync");
                Durable = ToArray();
                _device.Capture(this, "after-sync");
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count == 0) return;
                _device.Capture(this, "before-write");
                _device.Capture(this, "torn-write", buffer, offset, count);
                base.Write(buffer, offset, count);
            }

            public override void SetLength(long value)
            {
                _device.Capture(this, "before-truncate");
                base.SetLength(value);
                // A persisted truncation may precede a failed subsequent sync.
                var old = Durable;
                Array.Resize(ref Durable, checked((int)value));
                _device.Capture(this, "after-truncate");
                Durable = old;
            }
        }

        public void Dispose() { Data.Dispose(); Log.Dispose(); }
    }
}
