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
    /// </summary>
    internal sealed class SharedReaderRegistry
    {
        private static readonly int[] _none = new int[0];
        private readonly string _directory;
        private readonly Func<string, string, string[]> _getFiles;

        internal SharedReaderRegistry(string filename, Func<string, string, string[]> getFiles = null)
        {
            _directory = Path.GetFullPath(filename) + "-readers";
            _getFiles = getFiles ?? Directory.GetFiles;
        }

        internal IDisposable Register(int version)
        {
            if (this.LiveVersions() == null)
                throw new IOException("The shared-reader registry could not be inspected.");
            Directory.CreateDirectory(_directory);
            var name = version.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".lease";
            return new FileStream(Path.Combine(_directory, name), System.IO.FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
        }

        /// <summary>
        /// Experimental coordinator: register without first scanning the registry. The
        /// coordinator's scan still fails closed if the directory cannot be read. The
        /// file disappears when its handle closes, so closed leases never pile up.
        /// </summary>
        internal IDisposable RegisterUnscanned(int version)
        {
            var name = version.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".lease";
            var path = Path.Combine(_directory, name);
            try { return Create(path); }
            catch (DirectoryNotFoundException)
            {
                Directory.CreateDirectory(_directory);
                return Create(path);
            }

            static FileStream Create(string path) => new FileStream(path, System.IO.FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
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
                if (TryRemoveDeadLease(path)) continue;
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('-');
                // A malformed live lease has no usable snapshot floor. A synthetic
                // version cannot conservatively represent an unknown floor, so the
                // entire checkpoint must be skipped.
                if (separator <= 0 || !int.TryParse(name.Substring(0, separator),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return null;
                versions.Add(parsed);
            }
            if (versions.Count == 0) this.TryRemoveDirectory();
            return versions.ToArray();
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
