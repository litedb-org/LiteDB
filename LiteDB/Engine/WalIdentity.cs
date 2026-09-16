using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class WalIdentity
    {
        // Reserved v8/v9 header space: pragmas end at 108; recovery flag is 191.
        internal const int Position = 109;
        internal const int Size = 80;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LDBWAL01");
        private readonly byte[] _bytes;

        internal Guid Database { get; }
        internal Guid Epoch { get; }
        internal Guid Previous { get; }

        private WalIdentity(Guid database, Guid epoch, Guid previous, byte[] completedDigest)
        {
            Database = database;
            Epoch = epoch;
            Previous = previous;
            _bytes = new byte[Size];
            Buffer.BlockCopy(Magic, 0, _bytes, 0, Magic.Length);
            Buffer.BlockCopy(database.ToByteArray(), 0, _bytes, 8, 16);
            Buffer.BlockCopy(epoch.ToByteArray(), 0, _bytes, 24, 16);
            Buffer.BlockCopy(previous.ToByteArray(), 0, _bytes, 40, 16);
            Buffer.BlockCopy(completedDigest, 0, _bytes, 56, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(Checksum(_bytes)), 0, _bytes, 72, 8);
        }

        internal static WalIdentity Create() => new WalIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, new byte[16]);
        internal WalIdentity Next(Stream completedWal) => new WalIdentity(Database, Guid.NewGuid(), Epoch, Digest(completedWal));

        internal bool MatchesCompletedWal(Stream wal)
        {
            var digest = Digest(wal);
            for (var i = 0; i < digest.Length; i++)
                if (digest[i] != _bytes[56 + i]) return false;
            return true;
        }

        private static byte[] Digest(Stream wal)
        {
            if (wal.Length % PAGE_SIZE != 0) throw Invalid("Completed WAL has an incomplete page.");
            wal.Position = 0;
            using (var sha = SHA256.Create())
            {
                // Encrypted streams require page-aligned reads; ComputeHash's
                // implementation-specific buffer size does not honor that API.
                var page = new byte[PAGE_SIZE];
                while (wal.Position < wal.Length)
                {
                    if (wal.ReadFully(page, 0, page.Length) != page.Length)
                        throw Invalid("Completed WAL has an incomplete page.");
                    sha.TransformBlock(page, 0, page.Length, page, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var digest = new byte[16];
                Buffer.BlockCopy(sha.Hash, 0, digest, 0, digest.Length);
                return digest;
            }
        }

        internal void Write(PageBuffer header) => Buffer.BlockCopy(_bytes, 0, header.Array, header.Offset + Position, Size);

        internal static bool HasHeaderIdentity(PageBuffer page)
        {
            // Identity offsets are user payload in non-header pages. Require
            // the independent header signature before interpreting that payload.
            for (var i = 0; i < HeaderPage.HEADER_INFO.Length; i++)
                if (page[HeaderPage.P_HEADER_INFO + i] != (byte)HeaderPage.HEADER_INFO[i]) return false;
            for (var i = 0; i < Size; i++)
                if (page[Position + i] != 0) return true;
            return false;
        }

        internal static WalIdentity Read(PageBuffer header)
        {
            var bytes = new byte[Size];
            Buffer.BlockCopy(header.Array, header.Offset + Position, bytes, 0, Size);
            var empty = true;
            for (var i = 0; i < Size; i++) empty &= bytes[i] == 0;
            if (empty) return null;
            for (var i = 0; i < Magic.Length; i++)
                if (bytes[i] != Magic[i]) throw Invalid("Invalid database/WAL identity marker.");
            if (BitConverter.ToUInt64(bytes, 72) != Checksum(bytes))
                throw Invalid("Database/WAL identity checksum mismatch.");
            Guid ReadGuid(int offset)
            {
                var value = new byte[16];
                Buffer.BlockCopy(bytes, offset, value, 0, value.Length);
                return new Guid(value);
            }
            var digest = new byte[16];
            Buffer.BlockCopy(bytes, 56, digest, 0, digest.Length);
            var identity = new WalIdentity(ReadGuid(8), ReadGuid(24), ReadGuid(40), digest);
            if (identity.Database == Guid.Empty || identity.Epoch == Guid.Empty)
                throw Invalid("Incomplete database/WAL identity.");
            return identity;
        }

        private static ulong Checksum(byte[] bytes)
        {
            // Detect torn metadata; this is not an authenticity/security boundary.
            var hash = 14695981039346656037UL;
            for (var i = 0; i < 72; i++) hash = unchecked((hash ^ bytes[i]) * 1099511628211UL);
            return hash;
        }

        internal static PageBuffer ReadHeader(Stream stream)
        {
            var bytes = new byte[PAGE_SIZE];
            stream.Position = 0;
            if (stream.ReadFully(bytes, 0, bytes.Length) != bytes.Length)
                throw Invalid("Incomplete database/WAL header.");
            return new PageBuffer(bytes, 0, 0);
        }

        internal static LiteException Invalid(string message) => new LiteException(LiteException.INVALID_WAL,
            message + " Keep the data file and its matching WAL together; no WAL replay was performed.");
    }
}
