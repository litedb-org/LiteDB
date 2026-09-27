using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>Data-page permissions protected by the v10 header checksum.</summary>
    internal sealed class DataChecksumPolicy
    {
        // Separate from both pragmas and temporary journal descriptors (132..155).
        internal const int CoveragePosition = 160;
        internal const int LegacyBoundaryPosition = 161;
        // File version of the legacy pages kept by conversion: 8, 9, or 0 when unknown
        // (Complete coverage, or a conversion recorded before this byte existed).
        internal const int LegacyVersionPosition = 166;
        internal const byte CompleteMarker = 0x5A;
        internal const byte MixedMarker = 0xA5;
        internal bool Mixed { get; private set; }
        internal uint LegacyLastPageID { get; private set; }
        internal byte LegacyVersion { get; private set; }

        internal void InitializeMixed(uint lastPageID, byte legacyVersion)
        {
            Mixed = lastPageID != 0;
            LegacyLastPageID = lastPageID;
            LegacyVersion = Mixed ? legacyVersion : (byte)0;
        }

        internal void Load(BufferSlice header)
        {
            var coverage = header[CoveragePosition];
            if (coverage != CompleteMarker && coverage != MixedMarker)
                throw new PageChecksumException(FileOrigin.Data, 0);
            Mixed = coverage == MixedMarker;
            LegacyLastPageID = header.ReadUInt32(LegacyBoundaryPosition);
            LegacyVersion = header[LegacyVersionPosition];
            if ((!Mixed && LegacyLastPageID != 0) ||
                LegacyLastPageID > header.ReadUInt32(HeaderPage.P_LAST_PAGE_ID) ||
                (LegacyVersion != 0 && (!Mixed ||
                    (LegacyVersion != HeaderPage.FILE_VERSION && LegacyVersion != HeaderPage.VECTOR_FILE_VERSION))))
                throw new PageChecksumException(FileOrigin.Data, 0);
        }

        internal void Write(BufferSlice header)
        {
            header[CoveragePosition] = Mixed ? MixedMarker : CompleteMarker;
            header.Write(LegacyLastPageID, LegacyBoundaryPosition);
            header[LegacyVersionPosition] = LegacyVersion;
        }

        internal void Validate(BufferSlice page, long position)
        {
            // The legacy marker is four bits away from A5. Every single-bit
            // mutation of either marker fails closed, even inside the old range.
            if (Mixed && position > 0 && position / PAGE_SIZE <= LegacyLastPageID &&
                page[BasePage.P_PAGE_FORMAT] == PageChecksum.Legacy)
            {
                if (page.ReadUInt32(BasePage.P_PAGE_ID) != position / PAGE_SIZE)
                    throw new PageChecksumException(FileOrigin.Data, position);
                return;
            }
            PageChecksum.Validate(page, position);
        }
    }
}
