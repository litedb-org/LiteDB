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

        internal SharedReaderRegistry(string filename)
        {
            _directory = Path.GetFullPath(filename) + "-readers";
        }

        internal IDisposable Register(int version)
        {
            this.LiveVersions();
            Directory.CreateDirectory(_directory);
            var name = version.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".lease";
            return new FileStream(Path.Combine(_directory, name), System.IO.FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
        }

        internal int? OldestVersion()
        {
            var versions = this.LiveVersions();
            return versions.Length == 0 ? (int?)null : versions.Min();
        }

        /// <summary>
        /// Snapshot versions of every live reader. Dead leases are removed, and
        /// the directory with them once it is empty.
        /// </summary>
        internal int[] LiveVersions()
        {
            if (!Directory.Exists(_directory)) return _none;
            var versions = new List<int>();
            foreach (var path in Directory.GetFiles(_directory, "*.lease"))
            {
                if (TryRemoveDeadLease(path)) continue;
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('-');
                // An unreadable name pins everything: version zero fails closed.
                versions.Add(separator > 0 && int.TryParse(name.Substring(0, separator),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0);
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
