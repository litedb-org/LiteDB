using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB.Client.Shared
{
    /// <summary>The coordination ABI, independent of assembly and database format versions.</summary>
    internal static class SharedCoordinationProtocol
    {
        internal const long Magic = 0x004452485342444c; // LDBSHRD\0: stable envelope magic.
        internal const long LegacyMagic = 0x314452485342444c; // Unshipped, unversioned prototype.
        internal const long Version = 1;
        internal const int HeaderSize = 128;
        internal const int PageSize = 4096;
        internal const int SequenceOffset = HeaderSize;
        internal const int VersionOffset = HeaderSize + 8;
        internal const int StructuralOffset = HeaderSize + 16;
        internal const int EpochIdentityOffset = HeaderSize + 40;
        internal const int WriterHintOffset = HeaderSize + 48;

        internal sealed class Header
        {
            internal readonly byte[] Bytes;
            internal readonly bool Legacy;
            internal Header(byte[] bytes, bool legacy) { Bytes = bytes; Legacy = legacy; }

            internal bool SameAuthority(Header other)
            {
                if (Legacy || other.Legacy) return Legacy == other.Legacy;
                // Authority nonce and canonical database binding, never mutable epochs.
                for (var i = 48; i < 96; i++)
                    if (Bytes[i] != other.Bytes[i]) return false;
                return true;
            }
        }

        internal static byte[] CreateParticipation(string filename)
        {
            var bytes = new byte[HeaderSize];
            Write(bytes, 0, Magic);
            Write(bytes, 8, Version);
            Write(bytes, 16, HeaderSize);
            Write(bytes, 24, HeaderSize);
            // 32: required capabilities (none); 40: optional, non-semantic capabilities.
            Guid.NewGuid().ToByteArray().CopyTo(bytes, 48);
            DatabaseBinding(filename).CopyTo(bytes, 64);
            return bytes;
        }

        internal static byte[] CreatePage(Header participation)
        {
            var bytes = new byte[PageSize];
            participation.Bytes.CopyTo(bytes, 0);
            Write(bytes, 24, PageSize);
            Write(bytes, VersionOffset, -1);
            Write(bytes, EpochIdentityOffset, Read(Guid.NewGuid().ToByteArray(), 0) | 1);
            return bytes;
        }

        /// <summary>Only fully understood files can be attached or retired. Unknown bytes are preserved.</summary>
        internal static Header Inspect(FileStream file, bool page, string filename)
        {
            var length = file.Length;
            if (length != (page ? PageSize : HeaderSize) && (page || length != 8)) return null;
            var bytes = new byte[(int)length];
            file.Position = 0;
            var count = 0;
            while (count < bytes.Length)
            {
                var read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) return null;
                count += read;
            }
            if (Read(bytes, 0) == LegacyMagic)
            {
                if (!page && length != 8) return null;
                // The prototype used exactly eight words; extensions are not protocol zero.
                for (var i = 64; i < bytes.Length; i++) if (bytes[i] != 0) return null;
                return new Header(bytes, legacy: true);
            }
            if (length < HeaderSize || Read(bytes, 0) != Magic || Read(bytes, 8) != Version ||
                Read(bytes, 16) != HeaderSize || Read(bytes, 24) != length || Read(bytes, 32) != 0 ||
                (Read(bytes, 48) == 0 && Read(bytes, 56) == 0)) return null;
            for (var i = 96; i < HeaderSize; i++) if (bytes[i] != 0) return null;
            var binding = DatabaseBinding(filename);
            for (var i = 0; i < binding.Length; i++) if (bytes[64 + i] != binding[i]) return null;
            var header = new byte[HeaderSize];
            Buffer.BlockCopy(bytes, 0, header, 0, header.Length);
            return new Header(header, legacy: false);
        }

        internal static bool IsMarker(FileStream file)
        {
            if (file.Length != 8) return false;
            var bytes = new byte[8];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = file.Read(bytes, count, bytes.Length - count);
                if (read == 0) return false;
                count += read;
            }
            return Read(bytes, 0) == Magic || Read(bytes, 0) == LegacyMagic;
        }

        // This binds the namespace, not physical-file aliases. The fresh authority nonce,
        // OS-backed participation, protected database open and storage epochs bind lifetime.
        private static byte[] DatabaseBinding(string filename)
        {
            var path = Path.GetFullPath(filename);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) path = path.ToLowerInvariant();
            using (var hash = SHA256.Create()) return hash.ComputeHash(Encoding.UTF8.GetBytes(path));
        }

        internal static long Read(byte[] bytes, int offset)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++) value |= (ulong)bytes[offset + i] << (8 * i);
            return unchecked((long)value);
        }

        internal static void Write(byte[] bytes, int offset, long value)
        {
            for (var i = 0; i < 8; i++) bytes[offset + i] = (byte)(value >> (8 * i));
        }
    }
}
