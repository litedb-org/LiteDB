#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// A file whose device image takes only the writes made since the last successful sync, in order.
    /// A sync that fails with EIO (<see cref="FailNextSync"/>) forgets them, as Linux does ("fsyncgate"):
    /// they stay in the cache (the file's bytes) but no later sync writes them. A sync that answers
    /// "cannot sync" (#2242, <see cref="CannotSync"/>) keeps them pending: a later sync that succeeds
    /// still writes them. A power loss may leave any of the pending writes on the device: see
    /// <see cref="Image"/>. <see cref="CrashAtNextSync"/> models a process that dies right before a
    /// sync: it throws and leaves every write pending, dirty in the cache, for the next sync to write.
    /// Used as a caller stream, so every write of the engine goes through it.
    /// </summary>
    internal sealed class ForgetfulFile : FileStream
    {
        private readonly object _gate = new object();
        // Writes (a copy of their bytes) and SetLength calls since the last successful sync, each with
        // its number among all the operations made (see Operations).
        private readonly List<(long Position, byte[] Bytes, long Length, long Number)> _pending = new List<(long, byte[], long, long)>();
        private long _operations;
        private byte[] _durable;
        internal volatile bool FailNextSync, CannotSync, CrashAtNextSync;

        /// <summary>Called before each write with its position and length (the file's length is the one before it).</summary>
        internal Action<long, int> BeforeWrite;

        internal ForgetfulFile(string path)
            : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 1)
        {
            _durable = SyncPowerLossModel.ReadShared(path);
        }

        /// <summary>The bytes on the device: as of the last successful sync.</summary>
        internal byte[] Durable { get { lock (_gate) return _durable; } }

        /// <summary>The bytes in the cache: what every reader of the file sees.</summary>
        internal byte[] Live => SyncPowerLossModel.ReadShared(this.Name);

        /// <summary>Writes (and SetLength calls) a power loss would lose now.</summary>
        internal int Pending { get { lock (_gate) return _pending.Count; } }

        /// <summary>Writes and SetLength calls made so far: a mark for <see cref="Image"/>.</summary>
        internal long Operations { get { lock (_gate) return _operations; } }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var at = this.Position;
            BeforeWrite?.Invoke(at, count);
            base.Write(buffer, offset, count);
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            lock (_gate) _pending.Add((at, copy, -1, _operations++));
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
            lock (_gate)
            {
                if (CannotSync) throw new UnauthorizedAccessException("sync unsupported");
                if (CrashAtNextSync)
                {
                    CrashAtNextSync = false;
                    throw new IOException("modeled process crash before a sync");
                }
                if (FailNextSync)
                {
                    FailNextSync = false;
                    _pending.Clear();
                    throw new IOException("injected EIO: write-back failed and the pages were marked clean");
                }
                _durable = Apply(_durable, 0, torn: false);
                _pending.Clear();
            }
        }

        /// <summary>
        /// What a power loss may leave on the device now (see <see cref="ImageKind"/>). Pending writes
        /// made before the mark <paramref name="from"/> (<see cref="Operations"/> read earlier) never
        /// reach it: the OS wrote back the later ones first.
        /// </summary>
        internal byte[] Image(ImageKind kind, long from = 0)
        {
            lock (_gate)
            {
                if (kind == ImageKind.Lost || _pending.Count == 0) return _durable;
                return Apply(_durable, from, torn: kind == ImageKind.Torn);
            }
        }

        // The device image after the pending operations from the mark on reached it, the last write
        // torn: only its first half, sector aligned, written over what the device held. (A WAL write
        // is followed by the WAL's padding, a SetLength, which is not torn.)
        private byte[] Apply(byte[] durable, long from, bool torn)
        {
            var image = durable;
            var count = _pending.Count;
            var tornAt = count - 1;
            while (tornAt >= 0 && _pending[tornAt].Bytes == null) tornAt--;
            for (var i = 0; i < count; i++)
            {
                var (position, bytes, length, number) = _pending[i];
                if (number < from) continue;
                if (bytes == null)
                {
                    var resized = new byte[length];
                    Buffer.BlockCopy(image, 0, resized, 0, (int)Math.Min(length, image.Length));
                    image = resized;
                    continue;
                }
                var written = torn && i == tornAt ? Math.Max(1, bytes.Length / 2 / 512 * 512) : bytes.Length;
                if (written > bytes.Length) written = bytes.Length;
                var end = position + bytes.Length;
                if (end > image.Length)
                {
                    var grown = new byte[end];
                    Buffer.BlockCopy(image, 0, grown, 0, image.Length);
                    image = grown;
                }
                else if (ReferenceEquals(image, durable)) image = (byte[])image.Clone();
                Buffer.BlockCopy(bytes, 0, image, (int)position, written);
            }
            return image;
        }
    }

    /// <summary>A device image a power loss may leave (<see cref="ForgetfulFile.Image"/>).</summary>
    internal enum ImageKind
    {
        /// <summary>No write since the last successful sync reached the device.</summary>
        Lost,
        /// <summary>Every write since the last successful sync reached the device (the OS wrote it back).</summary>
        WrittenBack,
        /// <summary>Every such write reached the device, the last write torn in half (the rest as it was).</summary>
        Torn
    }
}
#endif
