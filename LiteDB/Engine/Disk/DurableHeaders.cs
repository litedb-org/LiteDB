using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LiteDB.Engine
{
    /// <summary>
    /// The data header each data file had at its latest successful sync in this process (a
    /// SHA-256 hash, by full path). While a file's header still matches, whatever an earlier
    /// engine left in its OS cache before that sync is durable (see DiskService.ProveDataFile).
    /// A missing or stale entry only costs a sync. Assumes a file at the same path with the same
    /// header is the same file: every WAL-emptying checkpoint, rebuild and new database draws a
    /// random salt, but a copy restored over the file (or a template) with a byte-identical header
    /// and different pages is taken as proven. Whoever replaces a database file must sync it
    /// (documented in the release notes); a replacement with another header is proven again.
    /// </summary>
    internal static class DurableHeaders
    {
        private const int Capacity = 1024;

        private static readonly Dictionary<string, byte[]> _hashes = new Dictionary<string, byte[]>(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        internal static void Record(string path, byte[] header)
        {
            var hash = Hash(header);
            lock (_hashes)
            {
                if (_hashes.Count >= Capacity && !_hashes.ContainsKey(path)) _hashes.Clear();
                _hashes[path] = hash;
            }
        }

        internal static bool Matches(string path, byte[] header)
        {
            byte[] hash;
            lock (_hashes)
            {
                if (!_hashes.TryGetValue(path, out hash)) return false;
            }
            return hash.SequenceEqual(Hash(header));
        }

        private static byte[] Hash(byte[] header)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(header);
        }
    }
}
