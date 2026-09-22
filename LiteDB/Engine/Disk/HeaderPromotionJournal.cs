using System;
using System.Linq;
using System.Security.Cryptography;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Two WAL padding pages containing a checksummed copy of the promoted data header.
    /// The zero page prefixes make these records invisible to transaction replay and checkpoint.
    /// Neither page is reused until checkpoint has synced the data and retired the WAL.
    /// </summary>
    internal static class HeaderPromotionJournal
    {
        private const int PayloadOffset = 128;
        private const int HalfPage = PAGE_SIZE / 2;
        private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("LDB-PROMOTE-0001");

        internal static byte[][] Encode(byte[] header, long position)
        {
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(header);
            var pages = new[] { new byte[PAGE_SIZE], new byte[PAGE_SIZE] };
            for (var i = 0; i < pages.Length; i++)
            {
                Buffer.BlockCopy(Magic, 0, pages[i], 32, Magic.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(position), 0, pages[i], 48, 8);
                pages[i][56] = (byte)i;
                Buffer.BlockCopy(hash, 0, pages[i], 64, hash.Length);
                Buffer.BlockCopy(header, i * HalfPage, pages[i], PayloadOffset, HalfPage);
            }
            return pages;
        }

        internal static bool IsPart(byte[] page, long position, int part)
        {
            if (page[56] != part || BitConverter.ToInt64(page, 48) != position) return false;
            for (var i = 0; i < 32; i++) if (page[i] != 0) return false;
            for (var i = 0; i < Magic.Length; i++) if (page[32 + i] != Magic[i]) return false;
            return true;
        }

        internal static byte[] Decode(byte[] first, byte[] second, long position)
        {
            if (!IsPart(first, position, 0) || !IsPart(second, position, 1)) return null;
            var header = new byte[PAGE_SIZE];
            Buffer.BlockCopy(first, PayloadOffset, header, 0, HalfPage);
            Buffer.BlockCopy(second, PayloadOffset, header, HalfPage, HalfPage);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(header);
                if (!hash.SequenceEqual(first.Skip(64).Take(32)) ||
                    !hash.SequenceEqual(second.Skip(64).Take(32))) return null;
            }
            // Validate the whole image, not merely the recovery record's checksum.
            var page = new HeaderPage(new PageBuffer(header, 0, 0));
            if (page.PageID != 0 || page.PageType != PageType.Header ||
                page.FileVersion < HeaderPage.VECTOR_FILE_VERSION) return null;
            return header;
        }
    }
}
