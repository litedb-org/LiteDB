using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// All registration and inspection runs under the database's named mutex.
    /// An exclusive open handle is the lease, not a heartbeat or a process ID.
    /// The OS releases it on death, including when no managed cleanup runs.
    /// A registry keeps one lease file for all its readers (<see cref="SharedReaderSlots"/>);
    /// a lease file named after a single version is still understood.
    /// </summary>
    internal sealed class SharedReaderRegistry : IDisposable
    {
        private static readonly int[] _none = new int[0];
        private readonly string _directory;
        private readonly Func<string, string, string[]> _getFiles;
        private readonly object _gate = new object();
        private SharedReaderSlots _slots;
        private bool _disposed;

        internal SharedReaderRegistry(string filename, Func<string, string, string[]> getFiles = null)
        {
            _directory = Path.GetFullPath(filename) + "-readers";
            _getFiles = getFiles ?? Directory.GetFiles;
        }

        /// <summary>
        /// Lease <paramref name="version"/> until the returned handle is disposed. The caller
        /// owns the mutex. A registry that cannot be inspected refuses the lease, and the
        /// caller streams under the mutex instead; otherwise nothing is scanned or removed
        /// here: checkpoints remove dead leases (crashed owners) themselves, and fail closed
        /// on a registry they cannot read. An exclusive open handle is the lease, as before:
        /// a prober's exclusive open fails while it is held (sharing violation, or LOCK_EX on
        /// Unix). DeleteOnClose removes the file only when its owner closes it (on Unix at
        /// Dispose, never while open), so a closed lease no longer has to be proven dead,
        /// deleted and its directory recreated by the next registration.
        /// </summary>
        internal IDisposable Register(int version)
        {
            // An open slot file already proved the registry usable.
            lock (_gate) if (_slots != null && !_disposed) return _slots.Lease(version);
            try { _getFiles(_directory, "*.lease"); }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("The shared-reader registry could not be inspected.", ex);
            }
            return this.RegisterUnscanned(version);
        }

        /// <summary>
        /// Create the lease file without inspecting the registry. <see cref="Register"/> checks
        /// that the registry is readable first; checkpoints' scans still fail closed if the
        /// directory cannot be read.
        /// </summary>
        internal IDisposable RegisterUnscanned(int version)
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(SharedReaderRegistry));
                if (_slots == null) _slots = SharedReaderSlots.Create(_directory);
                return _slots.Lease(version);
            }
        }

        /// <summary>Close the slot file once its last lease ends.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _slots?.Dispose();
            }
        }

        internal int? OldestVersion()
        {
            var versions = this.LiveVersions();
            // Unknown means a reader may exist. Version zero is only used by
            // callers as a conservative yes/no signal; checkpointing receives
            // the null result directly and skips all work.
            if (versions == null) return 0;
            return versions.Length == 0 ? (int?)null : versions.Min();
        }

        /// <summary>
        /// Snapshot versions of every live reader. Dead leases are removed, and
        /// the directory with them once it is empty.
        /// </summary>
        internal int[] LiveVersions()
        {
            string[] paths;
            try
            {
                // Directory.Exists folds access failures into false. Enumeration
                // distinguishes a definitely absent directory from an unreadable
                // registry, which must fail closed.
                paths = _getFiles(_directory, "*.lease");
            }
            catch (DirectoryNotFoundException) { return _none; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            var versions = new List<int>();
            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                var slots = name.StartsWith(SharedReaderSlots.Prefix, StringComparison.Ordinal);
                if (TryRemoveDeadLease(path))
                {
                    // A dead or closed slot lease takes its content file with it.
                    if (slots) TryRemoveUnheld(SharedReaderSlots.ContentPath(path));
                    continue;
                }
                if (slots)
                {
                    var held = SharedReaderSlots.ReadVersions(path);
                    if (held == null) return null;
                    versions.AddRange(held);
                    continue;
                }
                var separator = name.IndexOf('-');
                // A malformed live lease has no usable snapshot floor. A synthetic
                // version cannot conservatively represent an unknown floor, so the
                // entire checkpoint must be skipped.
                if (separator <= 0 || !int.TryParse(name.Substring(0, separator),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return null;
                versions.Add(parsed);
            }
            if (versions.Count == 0)
            {
                this.TryRemoveOrphanContent();
                this.TryRemoveDirectory();
            }
            return versions.ToArray();
        }

        /// <summary>
        /// Content files whose lease file is gone (an owner that died between creating the
        /// two, on Unix). Owners create both under the mutex that this scan also holds.
        /// </summary>
        private void TryRemoveOrphanContent()
        {
            string[] contents;
            try { contents = _getFiles(_directory, "*" + SharedReaderSlots.ContentExtension); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }
            foreach (var content in contents)
            {
                if (!File.Exists(Path.ChangeExtension(content, ".lease"))) TryRemoveUnheld(content);
            }
        }

        /// <summary>Delete a file only if nobody holds it open; best effort.</summary>
        private static void TryRemoveUnheld(string path)
        {
            try
            {
                using (new FileStream(path, System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool TryRemoveDeadLease(string path)
        {
            try
            {
                // Never unlink a live lease. On Unix an unlinked open file
                // would lose its registry identity while its owner still reads.
                using (new FileStream(path, System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(path);
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            // Sharing violations mean live. Any other failure to prove the lease
            // dead (delete-pending, a scanner's handle, permissions) fails closed.
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private void TryRemoveDirectory()
        {
            try { Directory.Delete(_directory, false); }
            // Best effort: a foreign file or handle keeps it; the next scan retries.
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
