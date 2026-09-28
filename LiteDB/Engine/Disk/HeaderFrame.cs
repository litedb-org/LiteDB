using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// The header frame (decisions 10 and 11 of docs/decisions/durability-policy.md): frame 0 of every
    /// WAL generation, from empty until it is emptied again, holds a copy of the data header as the
    /// data file held it when the generation started, with the salt that validates the frames after
    /// it. The WAL so describes itself: a commit does not depend on the data header being on the
    /// device, and recovery restores a data header that a power loss dropped (a data file created
    /// on storage that cannot sync, #2242, is left empty or with a page never written back) from it.
    /// Recovery skips it as a frame (<see cref="WalRetirementReader"/>). Volatile logs have none.
    /// </summary>
    internal static class HeaderFrame
    {
        /// <summary>The data header frame 0 of this raw WAL holds, or null when frame 0 is not a header frame.</summary>
        internal static byte[] Read(Stream raw)
        {
            if (raw == null || raw.Length < WalChecksum.FrameSize) return null;
            var frame = new byte[WalChecksum.FrameSize];
            raw.Position = 0;
            raw.ReadRequired(frame, 0, frame.Length);
            return WalChecksum.ReadHeaderFrame(frame);
        }

        /// <summary>
        /// Whether a data file's header page is one recovery reads as it is: a checksummed header whose
        /// checksum holds, or a legacy (v5) header, which has none. Only a header that is neither (an
        /// empty, torn or never-written page) is restored from a header frame. An intact header always
        /// wins, also one of another salt (a stale WAL generation, whose frames it then rejects): every
        /// header write after a database's creation goes through a synced header journal or waits
        /// for a data sync, so a header a power loss damaged is one that never synced.
        /// </summary>
        internal static bool IsIntact(byte[] header)
        {
            var page = new BufferSlice(header, 0, PAGE_SIZE);
            if (page.ReadUInt32(WalChecksum.MarkerPosition) == WalChecksum.HeaderMarker)
            {
                try
                {
                    PageChecksum.Validate(page, 0);
                    return true;
                }
                catch (PageChecksumException)
                {
                    return false;
                }
            }
            return page[HeaderPage.P_FILE_VERSION] < HeaderPage.CHECKSUM_FILE_VERSION &&
                string.CompareOrdinal(page.ReadString(HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length), HeaderPage.HEADER_INFO) == 0;
        }

        /// <summary>
        /// The data file must still hold every page this header names, the pages that were in it
        /// when the WAL generation started: the WAL holds only what changed since.
        /// </summary>
        internal static bool Fits(byte[] header, long dataLength) =>
            (new BufferSlice(header, 0, PAGE_SIZE).ReadUInt32(HeaderPage.P_LAST_PAGE_ID) + 1L) * PAGE_SIZE <= System.Math.Max(dataLength, PAGE_SIZE);
    }
}
