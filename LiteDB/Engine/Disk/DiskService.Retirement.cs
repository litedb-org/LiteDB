using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        internal object WalWriterLock => _writer.Value;
        internal WalRetirement Retirement => _checksums.Retirement;

        internal WalRetirement PrepareRetirement(IReadOnlyCollection<long> positions)
        {
            if (!ChecksumsEnabled || positions.Count == 0) return null;
            // Old readers must reject the representation before any witnesses
            // enter the WAL. This header-only promotion uses the existing journal.
            PromoteFileFormat(HeaderPage.MVCC_FILE_VERSION);
            var wanted = new HashSet<long>(positions);
            var witnesses = new List<WalRetirement.Witness>();
            foreach (var page in ReadFull(FileOrigin.Log))
            {
                if (page.WalFrame.Retired || !wanted.Remove(page.Position)) continue;
                witnesses.Add(new WalRetirement.Witness
                {
                    Position = page.Position, PageID = page.ReadUInt32(BasePage.P_PAGE_ID),
                    TransactionID = page.ReadUInt32(BasePage.P_TRANSACTION_ID),
                    PageType = page[BasePage.P_PAGE_TYPE], Confirmed = page.ReadBool(BasePage.P_IS_CONFIRMED),
                    Frame = page.WalFrame
                });
            }
            if (wanted.Count != 0) throw new PageChecksumException(FileOrigin.Log, wanted.First());
            var result = new WalRetirement
            {
                Root = Retirement.Root, RootCrc = Retirement.RootCrc, Sequence = _checksums.Sequence
            };
            var raw = ((ChecksummedWalStream)_writer.Value).RawStream;
            for (var start = 0; start < witnesses.Count; start += WalRetirement.Capacity)
            {
                var bytes = new byte[WalChecksum.FrameSize];
                var page = new BufferSlice(bytes, 0, PAGE_SIZE);
                var metadata = new BufferSlice(bytes, PAGE_SIZE, WalChecksum.MetadataSize);
                var position = Interlocked.Add(ref _logLength, PAGE_SIZE);
                page[BasePage.P_PAGE_FORMAT] = PageChecksum.Checksummed;
                page.Write(WalRetirement.Magic, 32);
                page.Write(result.Root, 36);
                page.Write(result.RootCrc, 44);
                var count = Math.Min(WalRetirement.Capacity, witnesses.Count - start);
                page.Write(count, 48);
                page.Write(result.Sequence, 52);
                for (var i = 0; i < count; i++)
                    WalRetirement.Encode(page, WalRetirement.EntriesPosition + i * WalRetirement.EntrySize, witnesses[start + i]);
                metadata.Write(WalChecksum.Magic, 0);
                Buffer.BlockCopy(_checksums.Salt, 0, bytes, PAGE_SIZE + 8, 16);
                metadata.Write(position, 24);
                metadata.Write(WalRetirement.Magic, 52);
                metadata.Write(WalRetirement.Crc(bytes), 4);
                raw.Position = position / PAGE_SIZE * WalChecksum.FrameSize;
                CheckpointStage("retirement-before-record-write");
                raw.Write(bytes, 0, bytes.Length);
                CheckpointStage("retirement-after-record-write");
                result.Root = position + PAGE_SIZE;
                result.RootCrc = WalRetirement.Crc(bytes);
            }
            SyncLogBarrier(raw);
            CheckpointStage("retirement-records-flushed");
            return result;
        }

        /// <summary>
        /// Data has been synced under a WAL-bound header journal. Publish the
        /// witness root, sync it, then durably remove that journal before allowing
        /// any old WAL bytes to change. Failure stops the engine with redo intact.
        /// </summary>
        internal void CompletePartialCheckpoint(WalRetirement pending)
        {
            var raw = ((ChecksummedWalStream)_writer.Value).RawStream;
            if (pending != null)
            {
                var data = _dataPool.Writer.Value;
                var header = new BufferSlice(new byte[PAGE_SIZE], 0, PAGE_SIZE);
                data.Position = 0;
                data.ReadRequired(header.Array, 0, PAGE_SIZE);
                PageChecksum.Validate(header, 0);
                header[HeaderPage.P_FILE_VERSION] = Math.Max(FileVersion, HeaderPage.MVCC_FILE_VERSION);
                pending.WriteHeader(header);
                PageChecksum.Write(header);
                data.Position = 0;
                CheckpointStage("retirement-before-header-write");
                data.Write(header.Array, 0, PAGE_SIZE);
                CheckpointStage("retirement-after-header-write");
                data.FlushToDisk();
                CheckpointStage("retirement-header-flushed");
                FileVersion = header[HeaderPage.P_FILE_VERSION];
                _checksums.Retirement = WalRetirement.Load(header, raw, _checksums);
                _checksums.Recovered(_checksums.Sequence, pending.End);
            }
            if (_checksums.JournalBytes != 0)
            {
                raw.SetLength(WalPadding.AlignedLength(raw.Length - _checksums.JournalBytes));
                CheckpointStage("retirement-before-journal-retire-flush");
                SyncLogBarrier(raw);
                _checksums.JournalBytes = 0;
                CheckpointStage("retirement-journal-retired");
            }
        }
    }
}
