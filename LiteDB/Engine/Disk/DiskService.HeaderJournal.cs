using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private byte[] _recoveredHeader;

        private void BeginHeaderJournal(byte[] header, bool conversion = false, bool promotion = false)
        {
            // Every caller overwrites existing data or its sole header. Commit
            // fallback may lose recent transactions, but must never let an
            // in-place overwrite proceed without durable recovery information.
            if (_checksums.JournalBytes != 0)
            {
                _writer.Value.FlushToDisk();
                return;
            }
            var log = ((ChecksummedWalStream)_writer.Value).RawStream;
            // Released engines trim unaligned WAL tails before checking the file version.
            if (ChecksumsEnabled)
            {
                WalPadding.Pad(log, log.Length / WalChecksum.FrameSize * WalChecksum.FrameSize, initialize: true);
                // A persisted footer must never depend on padding that existed
                // only in cache when its own write reached the device.
                log.FlushToDisk();
            }
            HeaderJournal.Write(log, header, conversion, _checksums, promotion);
            _checksums.JournalBytes = HeaderJournal.Size;
            log.FlushToDisk();
            ((ChecksummedWalFactory)_logFactory).SyncDirectory();
        }

        private void PrepareCheckpointHeader()
        {
            var header = new byte[PAGE_SIZE];
            var data = _dataPool.Writer.Value;
            data.Position = 0;
            data.ReadRequired(header, 0, header.Length);
            if (ChecksumsEnabled) PageChecksum.Validate(new BufferSlice(header, 0, PAGE_SIZE), 0);
            BeginHeaderJournal(header);
        }

        private void RecoverHeaderJournal(ref byte[] header)
        {
            if (!_logFactory.Exists() || _logFactory.GetLength() < PAGE_SIZE) return;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try
            {
                var journal = HeaderJournal.Read(reader.RawStream);
                if (journal == null) return;
                var published = journal.IsPublished(header);
                journal.ValidateCheckpointWal(reader.RawStream, published ? header : journal.Header);
                // An intent-only journal proves the primary still matches the
                // legacy header. Keep those exact bytes: rewriting the intent
                // metadata here can tear an AES block without sealed redo to
                // repair it after another crash.
                if (!published && !journal.IntentOnly)
                {
                    header = journal.Header;
                    _recoveredHeader = header;
                    LOG("Recovering database header from the durable WAL header journal", "RECOVERY");
                }
                _checksums.JournalBytes = journal.Legacy && published ? reader.RawStream.Length : journal.FooterBytes;
                if (journal.ConfirmsLegacyBackup && !published && journal.FooterBytes != 0)
                    _checksums.LegacyConfirmationPosition = journal.Position - PAGE_SIZE;
                if (_readOnly) return;

                // Repair and sync the header before removing its recovery copy.
                // Legacy redo stays until checkpoint also repairs converted pages.
                var data = _dataPool.Writer.Value;
                if (_recoveredHeader != null)
                {
                    // The selected journal may have survived only in the OS cache.
                    ((ChecksummedWalStream)_writer.Value).RawStream.FlushToDisk();
                    ((ChecksummedWalFactory)_logFactory).SyncDirectory();
                    this.CrashPoint("promotion-recovery-before-header-write");
                    data.Position = 0;
                    data.Write(header, 0, header.Length);
                    this.CrashPoint("promotion-recovery-after-header-write");
                }
                data.FlushToDisk();
                this.CrashPoint("promotion-recovery-after-header-flush");
                if (journal.Legacy && !published) return;
                var writer = ((ChecksummedWalStream)_writer.Value).RawStream;
                writer.SetLength(journal.Legacy ? 0 : WalPadding.AlignedLength(journal.Position));
                writer.FlushToDisk();
                _checksums.JournalBytes = 0;
                _recoveredHeader = null;
            }
            finally { _logPool.Return(reader); }
        }

        private void ReadRecoveredHeader(byte[] bytes, long position, FileOrigin origin)
        {
            if (origin == FileOrigin.Data && position == 0 && _recoveredHeader != null)
                Buffer.BlockCopy(_recoveredHeader, 0, bytes, 0, PAGE_SIZE);
        }
    }
}
