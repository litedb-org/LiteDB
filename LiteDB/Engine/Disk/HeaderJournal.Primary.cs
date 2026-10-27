using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class HeaderJournal
    {
        internal void ValidatePrimary(byte[] header)
        {
            if (Legacy) return;
            var page = new BufferSlice(header, 0, PAGE_SIZE);
            var version = header[HeaderPage.P_FILE_VERSION];
            // A torn modern header need not pass its CRC. Its format identity
            // can survive independently of the salt, including salt rotation.
            if (version >= HeaderPage.CHECKSUM_FILE_VERSION && version <= HeaderPage.CURRENT_FILE_VERSION &&
                (page.ReadUInt32(WalChecksum.MarkerPosition) == WalChecksum.HeaderMarker ||
                 header[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed)) return;

            // A torn version/format can instead be bound to this journal by the
            // preserved generation salt. Reserved zeroes in legacy data prove
            // nothing: require a nonzero salt as well as an exact match.
            var sameSalt = true;
            var nonzero = false;
            for (var i = 0; i < 16; i++)
            {
                var salt = Header[WalChecksum.SaltPosition + i];
                sameSalt &= header[WalChecksum.SaltPosition + i] == salt;
                nonzero |= salt != 0;
            }
            if (sameSalt && nonzero) return;

            throw new LiteException(LiteException.INVALID_DATABASE,
                "A modern header journal at plaintext offset {0} cannot repair this primary header: " +
                "its modern format or matching generation salt could not be established. " +
                "Neither file was changed; preserve both files for recovery.", Position);
        }
    }
}
