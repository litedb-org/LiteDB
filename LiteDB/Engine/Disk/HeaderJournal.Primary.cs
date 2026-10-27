using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class HeaderJournal
    {
        internal void ValidatePrimary(Stream stream, byte[] header)
        {
            if (Legacy) return;
            var page = new BufferSlice(header, 0, PAGE_SIZE);
            var version = header[HeaderPage.P_FILE_VERSION];
            var signature = page.ReadString(HeaderPage.P_HEADER_INFO, HeaderPage.HEADER_INFO.Length);
            // A valid journal cannot authorize replacing an identifiable legacy
            // primary. Check this before considering the journal's generation.
            if (signature == HeaderPage.HEADER_INFO && (version == 8 || version == 9))
                RefusePrimary();
            // Salt rotation is legitimate, but the immutable creation identity
            // of a checksum-valid published primary must still be the same.
            if (page.ReadUInt32(BasePage.P_TRANSACTION_ID) == PageChecksum.Compute(page) &&
                page.ReadInt64(HeaderPage.P_CREATION_TIME) !=
                new BufferSlice(Header, 0, PAGE_SIZE).ReadInt64(HeaderPage.P_CREATION_TIME))
                RefusePrimary();
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

            // The primary write may tear before *any* identity survives (AES
            // also damages the partially written block). ReadComplete validated
            // the backup header and footer CRC. Establish its redo authority
            // from every frame's generation, CRC and transaction witnesses, plus
            // the footer's binding of the complete physical WAL prefix. Merely
            // finding a modern-looking footer is not enough. Recognizable foreign
            // primaries were refused above; destroyed identity is recoverable
            // under the ordered, per-file torn-write model, not authentication
            // of arbitrary substituted files whose identity was also destroyed.
            if (nonzero && BodyChecksum == ComputeVerifiedBody(stream, Position, Header, null)) return;
            RefusePrimary();
        }

        private void RefusePrimary()
        {
            throw new LiteException(LiteException.INVALID_DATABASE,
                "A modern header journal at plaintext offset {0} cannot repair this primary header: " +
                "its primary identity or validated recovery generation could not be established. " +
                "Neither file was changed; preserve both files for recovery.", Position);
        }
    }
}
