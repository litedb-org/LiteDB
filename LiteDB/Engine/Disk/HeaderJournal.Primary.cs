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
            var checksumValid = page.ReadUInt32(BasePage.P_TRANSACTION_ID) == PageChecksum.Compute(page);
            var modernLayout = page.ReadUInt32(WalChecksum.MarkerPosition) == WalChecksum.HeaderMarker ||
                header[BasePage.P_PAGE_FORMAT] == PageChecksum.Checksummed;
            // A valid journal cannot authorize replacing an identifiable legacy
            // primary. Check this before considering the journal's generation.
            // v4 (file version 7) has its signature at a different offset,
            // even when encrypted. A raw v5+ encryption preamble is likewise
            // an identifiable primary, not an erased page from this journal.
            if (FileReaderV7.IsVersion(header) || HasEncryptedPreamble(header) ||
                (signature == HeaderPage.HEADER_INFO && version != 0 &&
                 (version < HeaderPage.CHECKSUM_FILE_VERSION || version > HeaderPage.CURRENT_FILE_VERSION) &&
                 (!modernLayout || checksumValid)))
                RefusePrimary();
            // Only a checksum-valid primary supplies a trustworthy creation
            // identity. A partial plaintext or AES-block tear can decode to valid
            // but wrong ticks, and the same is true of the generation salt.
            if (checksumValid && page.ReadInt64(HeaderPage.P_CREATION_TIME) !=
                new BufferSlice(Header, 0, PAGE_SIZE).ReadInt64(HeaderPage.P_CREATION_TIME))
                RefusePrimary();
            if (checksumValid)
            {
                // An intact matching primary already supplies authority. Never
                // use the journal-only fallback to override a valid header.
                if (signature == HeaderPage.HEADER_INFO && page.ReadUInt32(BasePage.P_PAGE_ID) == 0 &&
                    version >= HeaderPage.CHECKSUM_FILE_VERSION && version <= HeaderPage.CURRENT_FILE_VERSION)
                    return;
            }
            // No field of a checksum-invalid header can establish a foreign
            // database. Data/index pages have no database identity; WAL salts
            // bind frames to the journal, not to the primary. Validate journal
            // authority even when modern format or a matching salt survives.
            // This preserves torn-header recovery, but cannot authenticate a
            // substituted file whose header identity was also damaged.
            var nonzero = false;
            for (var i = 0; i < 16; i++) nonzero |= Header[WalChecksum.SaltPosition + i] != 0;
            if (nonzero && BodyChecksum == ComputeVerifiedBody(stream, Position, Header, null)) return;
            RefusePrimary();
        }

        private static bool HasEncryptedPreamble(byte[] header)
        {
            if (header[0] != 1) return false;
            // The raw encrypted header reserves bytes outside the salt and
            // password-check blocks. A lone marker-like byte in torn plaintext
            // is insufficient to identify an encryption preamble.
            for (var i = 1 + ENCRYPTION_SALT_SIZE; i < PAGE_SIZE; i++)
            {
                if (i >= AesPreamble.CHECK_START && i < AesPreamble.CHECK_START + AesPreamble.CHECK_SIZE) continue;
                if (header[i] != 0) return false;
            }
            return true;
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
