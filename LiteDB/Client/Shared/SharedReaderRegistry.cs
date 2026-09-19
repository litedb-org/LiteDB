using System;
using System.Globalization;
using System.IO;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// All registration and inspection runs under the database's named mutex.
    /// An exclusive open handle is the lease, not a heartbeat or a process ID.
    /// The OS releases it on death, including when no managed cleanup runs.
    /// </summary>
    internal sealed class SharedReaderRegistry
    {
        private readonly string _directory;

        internal SharedReaderRegistry(string filename)
        {
            _directory = Path.GetFullPath(filename) + "-readers";
        }

        internal IDisposable Register(int version)
        {
            this.OldestVersion();
            Directory.CreateDirectory(_directory);
            var name = version.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".lease";
            return new FileStream(Path.Combine(_directory, name), System.IO.FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
        }

        internal int? OldestVersion()
        {
            if (!Directory.Exists(_directory)) return null;
            int? oldest = null;
            foreach (var path in Directory.GetFiles(_directory, "*.lease"))
            {
                try
                {
                    // Never unlink a live lease. On Unix an unlinked open file
                    // would lose its registry identity while its owner still reads.
                    using (new FileStream(path, System.IO.FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    File.Delete(path);
                    continue;
                }
                catch (FileNotFoundException) { continue; }
                catch (IOException)
                {
                    // Sharing violations mean live; other I/O failures fail closed.
                }
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('-');
                var version = separator > 0 && int.TryParse(name.Substring(0, separator),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                oldest = oldest.HasValue ? Math.Min(oldest.Value, version) : version;
            }
            return oldest;
        }
    }
}
