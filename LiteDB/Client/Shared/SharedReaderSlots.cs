using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// One lease for all readers of a registry (connection) instead of one per reader. It is a
    /// pair of files. <c>slots-&lt;id&gt;.lease</c> is held exclusively (FileShare.None), exactly
    /// like a per-reader lease: a prober's exclusive open fails while it is held (sharing
    /// violation, LOCK_EX on Unix), and the OS releases it when the process dies.
    /// <c>slots-&lt;id&gt;.slots</c> carries fixed 8-byte slots naming the leased versions:
    /// <c>(v, ~v)</c> leases version v, <c>(0, 0)</c> is free. Any other content, including a
    /// slot torn by a concurrent rewrite, is unknown, and the scanning checkpoint fails closed.
    /// The content file cannot be the liveness proof itself: scanners must read it, and .NET on
    /// Unix takes no lock for a shared writable handle on network file systems.
    /// Registering rewrites one slot in place, so a leased scan creates and deletes no file.
    /// The name prefix makes older lease parsers fail closed instead of reading a version.
    /// </summary>
    internal sealed class SharedReaderSlots : IDisposable
    {
        internal const string Prefix = "slots-";
        internal const string ContentExtension = ".slots";
        private const int SlotSize = 8;
        private const int MaxSlots = 65536;

        private readonly object _gate = new object();
        private readonly FileStream _content;
        private readonly FileStream _lease;
        private readonly List<bool> _used = new List<bool>();
        private readonly byte[] _buffer = new byte[SlotSize];
        private int _inUse;
        private bool _disposeRequested;
        private bool _closed;
        private bool _lostPublishedContent;
#if DEBUG || TESTING
        internal Action<FileStream, byte[]> WriteOverride { get; set; }
        internal Action<FileStream, long> TruncateOverride { get; set; }
#endif

        private SharedReaderSlots(FileStream content, FileStream lease)
        {
            _content = content;
            _lease = lease;
        }

        /// <summary>The content file that belongs to a slot lease file.</summary>
        internal static string ContentPath(string leasePath) => Path.ChangeExtension(leasePath, ContentExtension);

        internal static SharedReaderSlots Create(string directory)
        {
            var lease = Path.Combine(directory, Prefix + Guid.NewGuid().ToString("N") + ".lease");
            try { return Open(lease); }
            catch (DirectoryNotFoundException)
            {
                Directory.CreateDirectory(directory);
                return Open(lease);
            }
        }

        private static SharedReaderSlots Open(string leasePath)
        {
            // Content first and closed last: a live lease file always has its content file.
            var content = new FileStream(ContentPath(leasePath), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read, 1, FileOptions.DeleteOnClose);
            try
            {
                // A count/complement header detects empty and whole-slot truncation too.
                content.Write(new byte[] { 0, 0, 0, 0, 255, 255, 255, 255 }, 0, SlotSize);
                var lease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                return new SharedReaderSlots(content, lease);
            }
            catch
            {
                content.Dispose();
                throw;
            }
        }

        /// <summary>Lease <paramref name="version"/> in a free slot until the result is disposed.</summary>
        internal IDisposable Lease(int version)
        {
            if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
            lock (_gate)
            {
                if (_closed) throw new ObjectDisposedException(nameof(SharedReaderSlots));
                if (_lostPublishedContent) throw new IOException("The shared-reader slots lost published content.");
                var index = _used.IndexOf(false);
                if (index < 0)
                {
                    index = _used.Count;
                    if (index == MaxSlots) throw new IOException("The shared-reader slot limit was reached.");
                    _used.Add(false);
                }
                // Written through to the OS before the caller releases the mutex, so the next
                // mutex owner's scan reads it.
                var headerStarted = false;
                try
                {
                    this.WriteSlot(index + 1, version, ~version);
                    // Also repair a header whose previous append failed. Registration and
                    // checkpoint inspection hold the database mutex, so append cannot race it.
                    if (_publishedCount != _used.Count)
                    {
                        headerStarted = true;
                        this.WriteSlot(0, _used.Count, ~_used.Count);
                        _publishedCount = _used.Count;
                    }
                }
                catch (IOException) { this.UndoRegistration(index, headerStarted); throw; }
                _used[index] = true;
                _inUse++;
                return new Slot(this, index);
            }
        }

        private int _publishedCount;

        /// <summary>Undo only unpublished content; failed cleanup must not replace the registration error.</summary>
        private void UndoRegistration(int index, bool headerStarted)
        {
            try
            {
                var length = (_publishedCount + 1L) * SlotSize;
                if (_content.Length < length)
                {
                    // Extending with zeroes would make lost live slots look free. Keep
                    // the file unknown and reject new leases until this connection closes.
                    _lostPublishedContent = true;
                    return;
                }
                if (index < _publishedCount) { this.WriteSlot(index + 1, 0, 0); return; }
#if DEBUG || TESTING
                if (this.TruncateOverride != null) this.TruncateOverride(_content, length);
                else
#endif
                _content.SetLength(length);
                // A failed header write may already have changed some or all count bytes.
                if (headerStarted) this.WriteSlot(0, _publishedCount, ~_publishedCount);
                _used.RemoveRange(_publishedCount, _used.Count - _publishedCount);
            }
            catch (IOException) { }
        }

        private void Release(int index)
        {
            lock (_gate)
            {
                if (_closed) return;
                _inUse--;
                // A slot that cannot be freed keeps naming its version and is never reused:
                // a stale lease only makes checkpoints conservative.
                try
                {
                    if (!_lostPublishedContent) this.WriteSlot(index + 1, 0, 0);
                    _used[index] = false;
                }
                catch (IOException) { }
                this.CloseIfDone();
            }
        }

        private void WriteSlot(int index, int version, int check)
        {
            WriteInt32(_buffer, 0, version);
            WriteInt32(_buffer, 4, check);
            _content.Position = (long)index * SlotSize;
#if DEBUG || TESTING
            if (this.WriteOverride != null) { this.WriteOverride(_content, _buffer); return; }
#endif
            _content.Write(_buffer, 0, SlotSize);
        }

        /// <summary>
        /// The versions a live slot lease names, or null when any slot is unknown (torn or
        /// malformed) or its content cannot be read: the caller then fails closed.
        /// </summary>
        internal static int[] ReadVersions(string leasePath)
        {
            byte[] content;
            try
            {
                using (var file = new FileStream(ContentPath(leasePath), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
                {
                    if (file.Length < SlotSize || file.Length > (MaxSlots + 1L) * SlotSize) return null;
                    content = new byte[file.Length];
                    var read = 0;
                    while (read < content.Length)
                    {
                        var n = file.Read(content, read, content.Length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    if (read != content.Length) return null;
                }
            }
            // A missing content file of a live lease is never expected: unknown.
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            // A partial trailing slot is a slot being appended: unknown.
            if (content.Length % SlotSize != 0) return null;
            var count = ReadInt32(content, 0);
            if (count < 0 || ReadInt32(content, 4) != ~count || count != content.Length / SlotSize - 1) return null;
            var versions = new List<int>();
            for (var offset = SlotSize; offset < content.Length; offset += SlotSize)
            {
                var version = ReadInt32(content, offset);
                var check = ReadInt32(content, offset + 4);
                if (version == 0 && check == 0) continue;
                if (version < 0 || check != ~version) return null;
                versions.Add(version);
            }
            return versions.ToArray();
        }

        /// <summary>Close once every lease has ended; a live reader keeps its lease valid.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposeRequested = true;
                this.CloseIfDone();
            }
        }

        private void CloseIfDone()
        {
            if (!_disposeRequested || _inUse > 0 || _closed) return;
            _closed = true;
            try { _lease.Dispose(); }
            finally { _content.Dispose(); }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static int ReadInt32(byte[] buffer, int offset) =>
            buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24;

        private sealed class Slot : IDisposable
        {
            private readonly SharedReaderSlots _owner;
            private readonly int _index;
            private int _released;

            internal Slot(SharedReaderSlots owner, int index)
            {
                _owner = owner;
                _index = index;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) _owner.Release(_index);
            }
        }
    }
}
