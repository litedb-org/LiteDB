using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Temporary WAL footer, synced before overwriting data. Legacy conversion
    /// uses staged intent, redo, preparation, and confirmation records so torn
    /// encrypted metadata cannot masquerade as a committed transaction.
    /// </summary>
    internal sealed partial class HeaderJournal
    {
        internal const int Size = 2 * PAGE_SIZE;
        private const long Magic = 0x314C4E524A42444C; // LDBJRNL1
        private const long PreparedMagic = 0x315045525042444C; // LDBPREP1
        private const long HeaderIntentMagic = 0x324E49474542444C; // LDBEGIN2: header-only backup
        private const long IntentMagic = 0x314E49474542444C; // LDBEGIN1
        private const int P_MAGIC = 132;
        private const int P_POSITION = 140;
        private const int P_CRC = 148;
        private const int P_BODY_CRC = 152;
        internal byte[] Header { get; private set; }
        internal long Position { get; private set; }
        internal bool Legacy => Header[HeaderPage.P_FILE_VERSION] < HeaderPage.CHECKSUM_FILE_VERSION;
        internal bool ConfirmsLegacyBackup { get; private set; }
        internal long FooterBytes { get; private set; } = Size;
        internal bool IntentOnly { get; private set; }
        private uint BodyChecksum { get; set; }

        internal void ValidateCheckpointWal(Stream stream, byte[] selectedHeader)
        {
            if (Legacy) return;
            // A new, validated salt proves data was synced before publication.
            // Otherwise this WAL can be needed to finish partial data overwrites;
            // discarding its damaged tail would certify a partial transaction.
            for (var i = 0; i < 16; i++)
                if (selectedHeader[WalChecksum.SaltPosition + i] != Header[WalChecksum.SaltPosition + i]) return;
            if (BodyChecksum != ComputeBody(stream, Position, out _))
                throw new PageChecksumException(FileOrigin.Log, 0);
        }

        internal bool IsPublished(byte[] header)
        {
            var page = new BufferSlice(header, 0, PAGE_SIZE);
            var valid = page.ReadUInt32(BasePage.P_TRANSACTION_ID) == PageChecksum.Compute(page) &&
                page.ReadUInt32(BasePage.P_PAGE_ID) == 0;
            if (valid && header[HeaderPage.P_FILE_VERSION] > HeaderPage.CHECKSUM_FILE_VERSION)
                throw LiteException.UnsupportedFileVersion(header[HeaderPage.P_FILE_VERSION]);
            var published = valid && header[HeaderPage.P_FILE_VERSION] == HeaderPage.CHECKSUM_FILE_VERSION;
            if (published)
            {
                PageChecksum.Validate(page, 0);
                new DataChecksumPolicy().Load(page);
                _ = new HeaderPage(new PageBuffer(header, 0, 0));
            }
            if (IntentOnly && !published)
                for (var i = 0; i < PAGE_SIZE; i++)
                {
                    if ((i >= BasePage.P_TRANSACTION_ID && i < BasePage.P_IS_CONFIRMED) || (i >= P_MAGIC && i < P_BODY_CRC + 4)) continue;
                    if (header[i] != Header[i]) throw new PageChecksumException(FileOrigin.Data, 0);
                }
            return published;
        }

        internal static HeaderJournal Read(Stream stream)
        {
            return ReadComplete(stream) ?? ReadPrepared(stream) ?? ReadIntent(stream);
        }

        private static HeaderJournal ReadIntent(Stream stream)
        {
            if (stream.Length < PAGE_SIZE) return null;
            var bytes = new byte[PAGE_SIZE];
            stream.Position = 0;
            stream.ReadRequired(bytes, 0, bytes.Length);
            var page = new BufferSlice(bytes, 0, PAGE_SIZE);
            var headerOnly = page.ReadInt64(P_MAGIC) == HeaderIntentMagic;
            if ((!headerOnly && page.ReadInt64(P_MAGIC) != IntentMagic) || (bytes[HeaderPage.P_FILE_VERSION] != 8 && bytes[HeaderPage.P_FILE_VERSION] != 9)) return null;
            var expected = page.ReadUInt32(P_CRC);
            page.Write(0u, P_CRC);
            var actual = ~Crc32C.Update(uint.MaxValue, bytes, 0, bytes.Length);
            page.Write(expected, P_CRC);
            if (expected != actual) return null;
            _ = new HeaderPage(new PageBuffer(bytes, 0, 0));
            // An older engine may append commits after a completed conversion
            // backup. Find its footer at the position derived from the intent;
            // those later legacy transactions must not be discarded with it.
            var footer = headerOnly ? 2L * PAGE_SIZE : ((long)page.ReadUInt32(HeaderPage.P_LAST_PAGE_ID) + 2) * PAGE_SIZE;
            if (stream.Length >= footer + Size)
            {
                var complete = ReadComplete(stream, footer);
                if (complete?.ConfirmsLegacyBackup == true) return complete;
                if (stream.Length > footer + Size) throw new PageChecksumException(FileOrigin.Log, footer);
            }
            // Redo has not been sealed. Ignore all of it, including torn AES
            // blocks whose random plaintext could otherwise look confirmed.
            return new HeaderJournal { Header = bytes, IntentOnly = true, FooterBytes = stream.Length };
        }

        private static HeaderJournal ReadComplete(Stream stream, long? recordPosition = null)
        {
            var position = recordPosition ?? stream.Length - Size;
            if (position < 0 || position % 16 != 0 || position > stream.Length - Size) return null;
            var bytes = new byte[Size];
            stream.Position = position;
            stream.ReadRequired(bytes, 0, bytes.Length);
            var descriptor = new BufferSlice(bytes, PAGE_SIZE, PAGE_SIZE);
            if (descriptor.ReadInt64(P_MAGIC) != Magic || descriptor.ReadInt64(P_POSITION) != position) return null;
            var expected = descriptor.ReadUInt32(P_CRC);
            descriptor.Write(0u, P_CRC);
            if (expected != ~Crc32C.Update(uint.MaxValue, bytes, 0, bytes.Length)) return null;
            var header = new byte[PAGE_SIZE];
            Buffer.BlockCopy(bytes, 0, header, 0, header.Length);
            var journal = new HeaderJournal
            {
                Header = header, Position = position, ConfirmsLegacyBackup = descriptor.ReadBool(BasePage.P_IS_CONFIRMED),
                BodyChecksum = descriptor.ReadUInt32(P_BODY_CRC),
                FooterBytes = position + Size == stream.Length ? Size : 0
            };
            var version = header[HeaderPage.P_FILE_VERSION];
            if (version != 8 && version != 9 && version != HeaderPage.CHECKSUM_FILE_VERSION) return null;
            if (position % (journal.Legacy ? PAGE_SIZE : WalChecksum.FrameSize) != 0) return null;
            if (!journal.Legacy)
            {
                var page = new BufferSlice(header, 0, PAGE_SIZE);
                PageChecksum.Validate(page, 0);
                new DataChecksumPolicy().Load(page);
            }
            else if (descriptor.ReadUInt32(P_BODY_CRC) != ComputeBody(stream, position, out _))
                throw new PageChecksumException(FileOrigin.Log, 0);
            if (journal.ConfirmsLegacyBackup && (!journal.Legacy || position < PAGE_SIZE)) return null;
            _ = new HeaderPage(new PageBuffer(header, 0, 0));
            return journal;
        }

        private static HeaderJournal ReadPrepared(Stream stream)
        {
            // The conversion's final legacy confirmation can itself tear. The
            // preceding prepared page and redo were synced first and carry an
            // independent CRC, so current readers never trust that torn commit.
            var last = stream.Length / PAGE_SIZE * PAGE_SIZE - PAGE_SIZE;
            for (var position = last; position >= 0 && position >= last - PAGE_SIZE; position -= PAGE_SIZE)
            {
                var bytes = new byte[PAGE_SIZE];
                stream.Position = position;
                stream.ReadRequired(bytes, 0, bytes.Length);
                var page = new BufferSlice(bytes, 0, PAGE_SIZE);
                if (page.ReadInt64(P_MAGIC) != PreparedMagic || page.ReadInt64(P_POSITION) != position || position < PAGE_SIZE) continue;
                var expected = page.ReadUInt32(P_CRC);
                page.Write(0u, P_CRC);
                var actual = ~Crc32C.Update(uint.MaxValue, bytes, 0, bytes.Length);
                page.Write(expected, P_CRC);
                if (expected != actual || (bytes[HeaderPage.P_FILE_VERSION] != 8 && bytes[HeaderPage.P_FILE_VERSION] != 9)) continue;
                if (page.ReadUInt32(P_BODY_CRC) != ComputeBody(stream, position, out _))
                    throw new PageChecksumException(FileOrigin.Log, 0);
                _ = new HeaderPage(new PageBuffer(bytes, 0, 0));
                return new HeaderJournal
                {
                    Header = bytes, Position = position, ConfirmsLegacyBackup = true,
                    FooterBytes = stream.Length - position
                };
            }
            return null;
        }

        internal static void Write(Stream stream, byte[] header, bool conversion, WalChecksum checksums)
        {
            var bytes = new byte[Size];
            Buffer.BlockCopy(header, 0, bytes, 0, PAGE_SIZE);
            if (conversion) Buffer.BlockCopy(header, 0, bytes, PAGE_SIZE, PAGE_SIZE);
            var descriptor = new BufferSlice(bytes, PAGE_SIZE, PAGE_SIZE);
            descriptor.Write(Magic, P_MAGIC);
            var length = stream.Length;
            descriptor.Write(length, P_POSITION);
            var transactionID = 0u;
            descriptor.Write(checksums.Enabled
                ? ComputeVerifiedBody(stream, length, header, checksums)
                : ComputeBody(stream, length, out transactionID), P_BODY_CRC);
            if (header[HeaderPage.P_FILE_VERSION] < HeaderPage.CHECKSUM_FILE_VERSION)
            {
                // Use a fresh, unconfirmed ID: legacy checkpoint must never copy
                // the footer, nor may a future transaction accidentally commit it.
                transactionID = conversion ? 1 : checked(transactionID + 1);
                new BufferSlice(bytes, 0, PAGE_SIZE).Write(transactionID, BasePage.P_TRANSACTION_ID);
                descriptor.Write(transactionID, BasePage.P_TRANSACTION_ID);
            }
            descriptor.Write(conversion, BasePage.P_IS_CONFIRMED);
            if (conversion)
            {
                var prepared = new BufferSlice(bytes, 0, PAGE_SIZE);
                prepared.Write(PreparedMagic, P_MAGIC);
                prepared.Write(length, P_POSITION);
                prepared.Write(descriptor.ReadUInt32(P_BODY_CRC), P_BODY_CRC);
                prepared.Write(0u, P_CRC);
                prepared.Write(~Crc32C.Update(uint.MaxValue, bytes, 0, PAGE_SIZE), P_CRC);
            }
            descriptor.Write(0u, P_CRC);
            descriptor.Write(~Crc32C.Update(uint.MaxValue, bytes, 0, bytes.Length), P_CRC);
            stream.Position = length;
            if (conversion)
            {
                // A legacy reader trusts confirmation bits. All redo and the
                // first footer page must reach storage BEFORE publishing one.
                stream.Write(bytes, 0, PAGE_SIZE);
                stream.FlushToDisk();
                stream.Write(bytes, PAGE_SIZE, PAGE_SIZE);
            }
            else stream.Write(bytes, 0, bytes.Length);
        }

        private static uint ComputeBody(Stream stream, long length, out uint transactionID)
        {
            var bytes = new byte[PAGE_SIZE];
            var crc = uint.MaxValue;
            transactionID = 0;
            stream.Position = 0;
            for (long position = 0; position < length; position += PAGE_SIZE)
            {
                var count = (int)Math.Min(bytes.Length, length - position);
                stream.ReadRequired(bytes, 0, count);
                if (count >= BasePage.P_TRANSACTION_ID + 4)
                    transactionID = Math.Max(transactionID, new BufferSlice(bytes, 0, PAGE_SIZE).ReadUInt32(BasePage.P_TRANSACTION_ID));
                crc = Crc32C.Update(crc, bytes, 0, count);
            }
            return ~crc;
        }

        /// <summary>Keep bounded legacy header redo while publishing mixed v10 coverage.</summary>
        internal static void BackupLegacyHeader(Stream log, byte[] header)
        {
            if (log.Length != 0) throw new IOException("Conversion requires a checkpointed WAL.");
            log.Position = 0;
            var page = new BufferSlice(new byte[PAGE_SIZE], 0, PAGE_SIZE);
            Buffer.BlockCopy(header, 0, page.Array, 0, PAGE_SIZE);
            page.Write(1u, BasePage.P_TRANSACTION_ID);
            page.Write(false, BasePage.P_IS_CONFIRMED);
            page.Write(HeaderIntentMagic, P_MAGIC);
            page.Write(0L, P_POSITION);
            page.Write(0u, P_BODY_CRC);
            page.Write(0u, P_CRC);
            page.Write(~Crc32C.Update(uint.MaxValue, page.Array, 0, PAGE_SIZE), P_CRC);
            // A torn first write must remain shorter than a legacy page. Once
            // its unconfirmed base header is durable, extending it cannot
            // accidentally publish a transaction, even with encrypted blocks.
            log.Write(page.Array, 0, 32);
            log.FlushToDisk();
            log.Write(page.Array, 32, PAGE_SIZE - 32);
            log.FlushToDisk();
            Buffer.BlockCopy(header, 0, page.Array, 0, PAGE_SIZE);
            page.Write(1u, BasePage.P_TRANSACTION_ID);
            page.Write(false, BasePage.P_IS_CONFIRMED);
            log.Write(page.Array, 0, PAGE_SIZE);
            // A surviving prepared record must imply that every redo page was
            // already durable, even if the subsequent preparation sync fails.
            log.FlushToDisk();
        }
    }
}
