using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>Shared by WAL streams; writes are serialized by DiskService.</summary>
    internal sealed class WalChecksum
    {
        internal const int MetadataSize = 64;
        internal const int FrameSize = PAGE_SIZE + MetadataSize;
        internal const uint Magic = 0x314C4157; // WAL1
        internal const int SaltPosition = 109; // Reserved database-header bytes 109..124.
        internal const int MarkerPosition = 125;
        internal const uint HeaderMarker = 0x31435243; // CRC1, independent of the version byte.
        internal byte[] Salt { get; private set; }
        internal WalRetirement Retirement { get; set; } = new WalRetirement();
        internal bool Enabled => Salt != null;
        internal long JournalBytes { get; set; }
        internal long LegacyConfirmationPosition { get; set; } = -1;
        internal long LastConfirmedPosition { get; private set; } = -PAGE_SIZE;
        internal long Sequence { get; set; }
        private readonly Dictionary<uint, Summary> _transactions = new Dictionary<uint, Summary>();
        private readonly Dictionary<long, ulong> _reusable = new Dictionary<long, ulong>();

        internal struct Summary
        {
            internal uint Count;
            internal ulong Digest;
        }

        internal struct Frame
        {
            internal bool RetirementRecord, Retired;
            internal ulong Contribution;
            internal uint Count;
            internal ulong Digest;
            internal long Sequence;
        }

        internal void Reset(byte[] salt)
        {
            Salt = salt;
            Retirement = new WalRetirement();
            LegacyConfirmationPosition = -1;
            Sequence = 0;
            LastConfirmedPosition = -PAGE_SIZE;
            _transactions.Clear();
            _reusable.Clear();
        }

        internal bool CanReuse(long position) => !Enabled || (position > LastConfirmedPosition && _reusable.ContainsKey(position));

        internal void Forget(uint transactionID)
        {
            if (!_transactions.Remove(transactionID)) return;
            // Other active transactions keep their summaries, but append on their
            // next safepoint. This bounds state retained by repeated rollbacks.
            _reusable.Clear();
        }

        internal Frame Prepare(BufferSlice page, BufferSlice metadata, long position)
        {
            page[BasePage.P_PAGE_FORMAT] = PageChecksum.Checksummed;
            metadata.Clear();
            metadata.Write(Magic, 0);
            Buffer.BlockCopy(Salt, 0, metadata.Array, metadata.Offset + 8, Salt.Length);
            metadata.Write(position, 24);
            var pageCrc = Crc32C.Update(uint.MaxValue, page.Array, page.Offset, PAGE_SIZE);
            var contribution = Contribution(pageCrc, metadata, position);
            _transactions.TryGetValue(page.ReadUInt32(BasePage.P_TRANSACTION_ID), out var summary);
            if (_reusable.TryGetValue(position, out var previous)) summary.Digest ^= previous;
            else summary.Count = checked(summary.Count + 1);
            summary.Digest ^= contribution;
            var sequence = page.ReadBool(BasePage.P_IS_CONFIRMED) ? checked(Sequence + 1) : 0;
            metadata.Write(summary.Count, 32);
            metadata.Write(unchecked((long)summary.Digest), 36);
            metadata.Write(sequence, 44);
            metadata.Write(~Crc32C.Update(pageCrc, metadata.Array, metadata.Offset, MetadataSize), 4);
            return new Frame { Contribution = contribution, Count = summary.Count, Digest = summary.Digest, Sequence = sequence };
        }

        internal void Accept(BufferSlice page, long position, Frame frame)
        {
            var transactionID = page.ReadUInt32(BasePage.P_TRANSACTION_ID);
            if (frame.Sequence != 0)
            {
                Sequence = frame.Sequence;
                LastConfirmedPosition = position;
                _transactions.Remove(transactionID);
                // Never rewrite a slot preceding an acknowledged commit. A torn
                // rewrite there would invalidate already-durable transactions.
                _reusable.Clear();
            }
            else
            {
                _transactions[transactionID] = new Summary { Count = frame.Count, Digest = frame.Digest };
                _reusable[position] = frame.Contribution;
            }
        }

        internal Frame Validate(BufferSlice page, BufferSlice metadata, long position)
        {
            if (page[BasePage.P_PAGE_FORMAT] != PageChecksum.Checksummed ||
                metadata.ReadUInt32(0) != Magic || metadata.ReadInt64(24) != position)
                throw new PageChecksumException(FileOrigin.Log, position);
            for (var i = 0; i < Salt.Length; i++)
                if (metadata[8 + i] != Salt[i]) throw new PageChecksumException(FileOrigin.Log, position);
            var expected = metadata.ReadUInt32(4);
            metadata.Write(0u, 4);
            var pageCrc = Crc32C.Update(uint.MaxValue, page.Array, page.Offset, PAGE_SIZE);
            var actual = ~Crc32C.Update(pageCrc, metadata.Array, metadata.Offset, MetadataSize);
            metadata.Write(expected, 4);
            if (expected != actual) throw new PageChecksumException(FileOrigin.Log, position);
            if (metadata.ReadUInt32(52) != 0 && metadata.ReadUInt32(52) != WalRetirement.Magic)
                throw new PageChecksumException(FileOrigin.Log, position);
            if (metadata.ReadUInt32(52) == WalRetirement.Magic &&
                (metadata.ReadUInt32(32) != 0 || metadata.ReadInt64(36) != 0 || metadata.ReadInt64(44) != 0 ||
                 page.ReadUInt32(BasePage.P_PAGE_ID) != 0 || page.ReadUInt32(BasePage.P_TRANSACTION_ID) != 0 ||
                 page[BasePage.P_PAGE_TYPE] != 0 || page[BasePage.P_IS_CONFIRMED] != 0))
                throw new PageChecksumException(FileOrigin.Log, position);
            return new Frame
            {
                RetirementRecord = metadata.ReadUInt32(52) == WalRetirement.Magic,
                Contribution = Contribution(pageCrc, metadata, position),
                Count = metadata.ReadUInt32(32), Digest = unchecked((ulong)metadata.ReadInt64(36)), Sequence = metadata.ReadInt64(44)
            };
        }

        private static ulong Contribution(uint pageCrc, BufferSlice metadata, long position)
        {
            // A nonlinear, position-dependent mix prevents identical changes in
            // two reused pages from cancelling as they would with XOR of CRCs.
            unchecked
            {
                var value = ((ulong)pageCrc << 32) ^ (ulong)position ^ (ulong)metadata.ReadInt64(8);
                value ^= value >> 30;
                value *= 0xBF58476D1CE4E5B9UL;
                value ^= value >> 27;
                value *= 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }

        internal void Recovered(long sequence, long end)
        {
            Sequence = sequence;
            // Abandoned transactions will never be resumed after open.
            LastConfirmedPosition = end - PAGE_SIZE;
        }
    }
}
