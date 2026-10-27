using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB.Client.Shared
{
    /// <summary>
    /// Bind shared admission to one canonical data/WAL/mutex path without storing
    /// path metadata. Bind mounts can preserve inode/nlink while realpath differs.
    /// </summary>
    internal static class DatabasePathLock
    {
        // Four disjoint domains, above the database address space and below the
        // admission/family bytes. One shared byte encodes 60 hash bits per domain.
        internal const long DomainLength = 1L << 60;

        internal static void Claim(DatabaseFileLock file, string filename)
        {
            if (DatabaseFileIdentity.Windows) filename = filename.ToUpperInvariant();
            using var sha = SHA256.Create();
            Claim(file, sha.ComputeHash(Encoding.UTF8.GetBytes(filename)));
        }

        internal static void Claim(DatabaseFileLock file, byte[] fingerprint)
        {
            var points = new long[4];
            for (var i = 0; i < points.Length; i++)
            {
                ulong value = 0;
                for (var j = 7; j >= 0; j--) value = (value << 8) | fingerprint[i * 8 + j];
                points[i] = checked((i + 1) * DomainLength + (long)(value & (ulong)(DomainLength - 1)));
                file.Lock(points[i], exclusive: false);
            }
            // Claim every byte before checking. Different concurrent claimants
            // may both fail, but cannot both pass even without the bootstrap mutex.
            for (var i = 0; i < points.Length; i++)
            {
                var start = (i + 1) * DomainLength;
                var end = start + DomainLength;
                var point = points[i];
                // A zero-length Unix range means through EOF, not an empty range.
                if ((point > start && file.Conflicts(start, point - start)) ||
                    (point + 1 < end && file.Conflicts(point + 1, end - point - 1)))
                    throw new IOException("The physical database is already open through a different canonical path. " +
                        "Use one path for all connections so data, WAL and Shared mutex identities agree.");
            }
        }
    }
}
