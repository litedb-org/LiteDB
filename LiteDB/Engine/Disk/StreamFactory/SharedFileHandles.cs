using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LiteDB.Engine
{
    /// <summary>
    /// Keeps a shared connection's data and log file handles open between its short-lived
    /// engines, so an operation does not pay for opening files. Only OS handles are reused:
    /// every engine still rebuilds its WAL index, cache and header from the files.
    /// Each lease owns one OS handle exclusively (the OS file pointer is per handle), and a
    /// handle is reused only while it still names the file at its original path: a deleted,
    /// replaced or renamed file is closed and the path opened again.
    /// Windows only; elsewhere every lease opens the path, as without the cache.
    /// [ThreadSafe]
    /// </summary>
    internal sealed class SharedFileHandles : IDisposable
    {
        // Every process caching handles must allow the others to open, write, truncate and
        // delete the files. Exclusive openers (direct mode, FileShare.Read writers) are
        // refused while a shared connection holds a writable handle.
        internal const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

        private const int MAX_IDLE_PER_FILE = 4;

        private readonly Dictionary<Key, Stack<Entry>> _idle = new Dictionary<Key, Stack<Entry>>();
        private readonly object _lock = new object();
        private int _generation;
        private bool _disposed;

        internal static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

#if DEBUG || TESTING
        /// <summary>Test hook: the volume's POSIX-delete capability for a database path, or null for the real answer.</summary>
        internal static Func<string, bool?> SimulatePosixDelete;
#endif

        /// <summary>
        /// True when handles for <paramref name="filename"/> may be cached. The final checkpoint
        /// deletes the WAL while other connections keep idle handles open. With POSIX delete
        /// semantics the name disappears at once and the next open creates a fresh file; with
        /// legacy delete-pending semantics (FAT, exFAT, many SMB and third-party providers) the
        /// name stays pending until every handle closes, so the next OpenOrCreate fails with
        /// access denied until an idle peer happens to run its next operation. Such volumes
        /// keep opening files per operation.
        /// </summary>
        internal static bool IsSupportedFor(string filename)
        {
            if (!IsSupported) return false;
#if DEBUG || TESTING
            var simulated = SimulatePosixDelete?.Invoke(filename);
            if (simulated.HasValue) return simulated.Value;
#endif
            try
            {
                return Native.SupportsPosixDelete(Path.GetFullPath(filename));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Also keep writable handles. That refuses every other opener that does not share
        /// write access (file copies, backups, direct mode) for the connection's lifetime,
        /// not only during an operation. Off: writers open per operation as before.
        /// </summary>
        internal static bool CacheWriters = true;

#if DEBUG || TESTING
        internal int Opened;
        internal int Reused;
        internal int Invalidated;
#endif

        /// <summary>
        /// Lease a stream on <paramref name="path"/>. Disposing it returns the handle.
        /// </summary>
        internal FileStream Open(string path, FileMode mode, FileAccess access, int bufferSize, FileOptions options)
        {
            var key = new Key(path, access, options);
            Entry entry = null;
            int generation;
            while (true)
            {
                Entry candidate = null;
                lock (_lock)
                {
                    // A closing engine may still open streams after the connection disposed.
                    if (_disposed) return new FileStream(path, mode, access, Share, bufferSize, options);
                    generation = _generation;
                    if (_idle.TryGetValue(key, out var idle) && idle.Count > 0) candidate = idle.Pop();
                }
                if (candidate == null) break;

                // The identity check and CloseHandle are kernel calls that a slow volume can
                // stall, so they run outside the lock that every stream of this connection uses.
                var valid = candidate.StillNamesItsPath();
                lock (_lock) valid &= generation == _generation;
                if (valid)
                {
                    entry = candidate;
                    break;
                }
                candidate.Close();
#if DEBUG || TESTING
                lock (_lock) Invalidated++;
#endif
            }

            if (entry == null)
            {
                entry = Entry.Open(path, mode, access, options);
#if DEBUG || TESTING
                lock (_lock) Opened++;
#endif
            }
#if DEBUG || TESTING
            else lock (_lock) Reused++;
#endif

            try
            {
                return new LeasedFileStream(this, key, entry, generation, access, bufferSize);
            }
            catch
            {
                entry.Close();
                throw;
            }
        }

        /// <summary>
        /// Close idle handles, for example before this connection replaces the files.
        /// Leased handles close when their streams are disposed.
        /// </summary>
        internal void CloseIdle()
        {
            lock (_lock)
            {
                _generation++;
                foreach (var idle in _idle.Values)
                {
                    while (idle.Count > 0) idle.Pop().Close();
                }
                _idle.Clear();
            }
        }

        private void Return(Key key, Entry entry, int generation)
        {
            lock (_lock)
            {
                if (!_disposed && generation == _generation)
                {
                    if (!_idle.TryGetValue(key, out var idle)) _idle[key] = idle = new Stack<Entry>();
                    if (idle.Count < MAX_IDLE_PER_FILE)
                    {
                        idle.Push(entry);
                        return;
                    }
                }
            }
            entry.Close();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            this.CloseIdle();
        }

        private readonly struct Key : IEquatable<Key>
        {
            private readonly string _path;
            private readonly FileAccess _access;
            private readonly FileOptions _options;

            internal Key(string path, FileAccess access, FileOptions options)
            {
                _path = path;
                _access = access;
                _options = options;
            }

            public bool Equals(Key other) =>
                _access == other._access && _options == other._options &&
                string.Equals(_path, other._path, StringComparison.OrdinalIgnoreCase);

            public override bool Equals(object obj) => obj is Key other && this.Equals(other);

            public override int GetHashCode() =>
                StringComparer.OrdinalIgnoreCase.GetHashCode(_path) ^ ((int)_access << 20) ^ (int)_options;
        }

        /// <summary>
        /// One OS handle, owned by the cache. Its identity is the normalized path the file
        /// had when the path was opened.
        /// </summary>
        private sealed class Entry
        {
            private readonly FileStream _owner;
            private readonly string _finalPath;

            private Entry(FileStream owner, string finalPath)
            {
                _owner = owner;
                _finalPath = finalPath;
            }

            internal SafeFileHandle Handle => _owner.SafeFileHandle;

            internal static Entry Open(string path, FileMode mode, FileAccess access, FileOptions options)
            {
                // bufferSize 1: the owner never performs I/O; leases bring their own buffers.
                var owner = new FileStream(path, mode, access, Share, 1, options);
                try
                {
                    var finalPath = IsSupported ? Native.FinalPath(owner.SafeFileHandle) : null;
                    return new Entry(owner, finalPath);
                }
                catch
                {
                    owner.Dispose();
                    throw;
                }
            }

            /// <summary>
            /// True while this handle's file is still the file at its original path: not
            /// deleted (POSIX or pending delete), not renamed away or replaced.
            /// </summary>
            internal bool StillNamesItsPath()
            {
                if (_finalPath == null) return false;
                try
                {
                    if (!Native.IsLinked(_owner.SafeFileHandle)) return false;
                    var current = Native.FinalPath(_owner.SafeFileHandle);
                    return string.Equals(current, _finalPath, StringComparison.OrdinalIgnoreCase);
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }

            internal void Close() => _owner.Dispose();
        }

        /// <summary>
        /// A FileStream over a cached handle that it does not own. Each lease has its own
        /// buffer and position; disposing it returns the handle to the cache.
        /// </summary>
        private sealed class LeasedFileStream : FileStream
        {
            private readonly SharedFileHandles _cache;
            private readonly Key _key;
            private readonly int _generation;
            private Entry _entry;

            internal LeasedFileStream(SharedFileHandles cache, Key key, Entry entry, int generation,
                FileAccess access, int bufferSize)
                : base(new SafeFileHandle(entry.Handle.DangerousGetHandle(), false), access, bufferSize)
            {
                _cache = cache;
                _key = key;
                _entry = entry;
                _generation = generation;
                // A reused handle keeps the previous lease's OS file pointer.
                this.Position = 0;
            }

            protected override void Dispose(bool disposing)
            {
                try
                {
                    base.Dispose(disposing);
                }
                finally
                {
                    var entry = _entry;
                    _entry = null;
                    if (entry != null)
                    {
                        if (disposing) _cache.Return(_key, entry, _generation);
                        else entry.Close();
                    }
                }
            }
        }

        private static class Native
        {
            private const int FileStandardInfo = 1;
            private const int MAX_PATH_BUFFER = 1024;

            [StructLayout(LayoutKind.Sequential)]
            private struct FILE_STANDARD_INFO
            {
                internal long AllocationSize;
                internal long EndOfFile;
                internal uint NumberOfLinks;
                [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
                [MarshalAs(UnmanagedType.U1)] internal bool Directory;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
                out FILE_STANDARD_INFO info, int size);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern int GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path,
                int length, int flags);

            private const uint FILE_SUPPORTS_POSIX_UNLINK_RENAME = 0x00000400;

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePath, int length);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetVolumeInformationW(string rootPath, StringBuilder volumeName, int volumeNameLength,
                out uint serialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
                StringBuilder fileSystemName, int fileSystemNameLength);

            internal static bool SupportsPosixDelete(string fullPath)
            {
                var root = new StringBuilder(MAX_PATH_BUFFER);
                if (!GetVolumePathNameW(fullPath, root, root.Capacity)) return false;
                if (!GetVolumeInformationW(root.ToString(), null, 0, out _, out _, out var flags, null, 0)) return false;
                return (flags & FILE_SUPPORTS_POSIX_UNLINK_RENAME) != 0;
            }

            internal static bool IsLinked(SafeFileHandle handle)
            {
                if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out var info, Marshal.SizeOf<FILE_STANDARD_INFO>()))
                    return false;
                return !info.DeletePending && info.NumberOfLinks > 0;
            }

            internal static string FinalPath(SafeFileHandle handle)
            {
                var buffer = new StringBuilder(MAX_PATH_BUFFER);
                var length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                if (length <= 0 || length >= buffer.Capacity)
                {
                    if (length <= 0) throw new IOException("Cannot resolve the path of a cached file handle.",
                        Marshal.GetHRForLastWin32Error());
                    buffer = new StringBuilder(length + 1);
                    length = GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
                    if (length <= 0 || length >= buffer.Capacity)
                        throw new IOException("Cannot resolve the path of a cached file handle.");
                }
                return buffer.ToString();
            }
        }
    }
}
