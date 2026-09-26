using System;
using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Durable witnesses for retired WAL frames. The checksummed data header names
    /// the chain root. Witnesses retain the original transaction proof after its
    /// payload becomes unreachable by every snapshot and its slot is reused.
    /// </summary>
    internal sealed class WalRetirement
    {
        internal const uint Magic = 0x31544552; // RET1
        internal const int RootPosition = 168;
        internal const int EntrySize = 56;
        internal const int EntriesPosition = 64;
        internal const int Capacity = (PAGE_SIZE - EntriesPosition) / EntrySize;
        internal long Root;
        internal uint RootCrc;
        internal long Sequence;
        internal long End => Root == 0 ? 0 : Root;
        internal readonly Dictionary<long, List<Witness>> Slots = new Dictionary<long, List<Witness>>();
        internal readonly HashSet<long> Records = new HashSet<long>();

        internal sealed class Witness
        {
            internal long Position;
            internal uint PageID, TransactionID;
            internal byte PageType;
            internal bool Confirmed;
            internal WalChecksum.Frame Frame;

            internal PageBuffer Replay()
            {
                var page = new PageBuffer(new byte[PAGE_SIZE], 0, 0) { Position = Position, Origin = FileOrigin.Log, WalFrame = Frame };
                page.WalFrame.Retired = true;
                page.Write(PageID, BasePage.P_PAGE_ID);
                page.Write(TransactionID, BasePage.P_TRANSACTION_ID);
                page.Write(PageType, BasePage.P_PAGE_TYPE);
                page.Write(Confirmed, BasePage.P_IS_CONFIRMED);
                return page;
            }

            internal bool Matches(PageBuffer page) => TransactionID == page.ReadUInt32(BasePage.P_TRANSACTION_ID) &&
                Frame.Contribution == page.WalFrame.Contribution;
        }

        internal void WriteHeader(BufferSlice header)
        {
            header.Write(Root, RootPosition);
            header.Write(RootCrc, RootPosition + 8);
            header.Write(Sequence, RootPosition + 12);
        }

        internal static WalRetirement Load(BufferSlice header, Stream raw, WalChecksum checksum)
        {
            var result = new WalRetirement();
            if (header[HeaderPage.P_FILE_VERSION] < HeaderPage.MVCC_FILE_VERSION) return result;
            result.Root = header.ReadInt64(RootPosition);
            result.RootCrc = header.ReadUInt32(RootPosition + 8);
            result.Sequence = header.ReadInt64(RootPosition + 12);
            if (result.Root == 0)
            {
                if (result.RootCrc != 0 || result.Sequence != 0) throw new PageChecksumException(FileOrigin.Data, 0);
                return result;
            }
            if (result.Sequence < 1 || raw == null) throw new PageChecksumException(FileOrigin.Data, 0);
            var next = result.Root;
            var expected = result.RootCrc;
            var maximumSequence = result.Sequence;
            var bytes = new byte[WalChecksum.FrameSize];
            // A slot reused under long-lived readers accumulates witnesses across
            // records; keep duplicate detection constant-time per witness.
            var slotTransactions = new Dictionary<long, HashSet<uint>>();
            while (next != 0)
            {
                var position = next - PAGE_SIZE;
                if (position < 0 || position % PAGE_SIZE != 0 ||
                    position / PAGE_SIZE >= raw.Length / WalChecksum.FrameSize || !result.Records.Add(position))
                    throw new PageChecksumException(FileOrigin.Log, position);
                raw.Position = position / PAGE_SIZE * WalChecksum.FrameSize;
                raw.ReadRequired(bytes, 0, bytes.Length);
                if (Crc(bytes) != expected) throw new PageChecksumException(FileOrigin.Log, position);
                var page = new BufferSlice(bytes, 0, PAGE_SIZE);
                var frame = checksum.Validate(page, new BufferSlice(bytes, PAGE_SIZE, WalChecksum.MetadataSize), position);
                if (!frame.RetirementRecord || page.ReadUInt32(32) != Magic ||
                    page.ReadInt64(52) < 1 || page.ReadInt64(52) > maximumSequence ||
                    (next == result.Root && page.ReadInt64(52) != result.Sequence))
                    throw new PageChecksumException(FileOrigin.Log, position);
                maximumSequence = page.ReadInt64(52);
                var count = page.ReadInt32(48);
                if (count < 1 || count > Capacity) throw new PageChecksumException(FileOrigin.Log, position);
                for (var i = 0; i < count; i++)
                {
                    var witness = Decode(page, EntriesPosition + i * EntrySize);
                    if (witness.Position < 0 || witness.Position >= position || witness.Position % PAGE_SIZE != 0 ||
                        witness.TransactionID == 0 || witness.Frame.Count == 0 || witness.PageType > (byte)PageType.Schema ||
                        page[EntriesPosition + i * EntrySize + 17] > 1 ||
                        (witness.Confirmed ? witness.Frame.Sequence < 1 || witness.Frame.Sequence > result.Sequence : witness.Frame.Sequence != 0))
                        throw new PageChecksumException(FileOrigin.Log, position);
                    if (!result.Slots.TryGetValue(witness.Position, out var entries))
                    {
                        result.Slots.Add(witness.Position, entries = new List<Witness>());
                        slotTransactions.Add(witness.Position, new HashSet<uint>());
                    }
                    if (!slotTransactions[witness.Position].Add(witness.TransactionID))
                        throw new PageChecksumException(FileOrigin.Log, position);
                    entries.Add(witness);
                }
                next = page.ReadInt64(36);
                expected = page.ReadUInt32(44);
                if (next > position || (next == 0 && expected != 0))
                    throw new PageChecksumException(FileOrigin.Log, position);
            }
            foreach (var position in result.Records)
                if (result.Slots.ContainsKey(position)) throw new PageChecksumException(FileOrigin.Log, position);
            return result;
        }

        internal static uint Crc(byte[] bytes) => ~Crc32C.Update(uint.MaxValue, bytes, 0, bytes.Length);

        internal static Witness Decode(BufferSlice page, int offset) => new Witness
        {
            Position = page.ReadInt64(offset), PageID = page.ReadUInt32(offset + 8),
            TransactionID = page.ReadUInt32(offset + 12), PageType = page[offset + 16],
            Confirmed = page.ReadBool(offset + 17),
            Frame = new WalChecksum.Frame
            {
                Contribution = unchecked((ulong)page.ReadInt64(offset + 24)), Count = page.ReadUInt32(offset + 32),
                Digest = unchecked((ulong)page.ReadInt64(offset + 36)), Sequence = page.ReadInt64(offset + 44)
            }
        };

        internal static void Encode(BufferSlice page, int offset, Witness value)
        {
            page.Write(value.Position, offset);
            page.Write(value.PageID, offset + 8);
            page.Write(value.TransactionID, offset + 12);
            page[offset + 16] = value.PageType;
            page.Write(value.Confirmed, offset + 17);
            page.Write(unchecked((long)value.Frame.Contribution), offset + 24);
            page.Write(value.Frame.Count, offset + 32);
            page.Write(unchecked((long)value.Frame.Digest), offset + 36);
            page.Write(value.Frame.Sequence, offset + 44);
        }
    }
}
