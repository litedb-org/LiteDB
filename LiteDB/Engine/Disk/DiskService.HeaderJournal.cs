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
            // fallback may lose recent transactions, but a failed sync must never
            // let an in-place overwrite proceed without durable recovery information.
            // Storage that cannot sync at all proceeds in write order (#2242).
            if (_checksums.JournalBytes != 0)
            {
                SyncLogBarrier(_writer.Value);
                return;
            }
            var log = ((ChecksummedWalStream)_writer.Value).RawStream;
            // Keep the footer aligned too: released engines trim unaligned WAL
            // tails before checking the primary header's newer format version.
            if (ChecksumsEnabled)
            {
                WalPadding.Pad(log, log.Length / WalChecksum.FrameSize * WalChecksum.FrameSize, initialize: true);
                // A persisted footer must never depend on padding that existed
                // only in cache when its own write reached the device.
                SyncLogBarrier(log);
            }
            HeaderJournal.Write(log, header, conversion, _checksums, promotion, SyncLogBarrier);
            _checksums.JournalBytes = HeaderJournal.Size;
            SyncLogBarrier(log);
            SyncLogDirectory();
        }

        private void PrepareCheckpointHeader()
        {
            var header = new byte[PAGE_SIZE];
            this.UseDataWriter(data =>
            {
                data.Position = 0;
                data.ReadRequired(header, 0, header.Length);
            });
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
                // Unsealed conversion records followed by legacy commits: recover
                // the whole WAL by legacy rules; conversion restarts after checkpoint.
                if (journal.LegacyTail) return;
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
                // Validate a published witness root before retiring the only
                // header recovery copy. CRC-valid malformed root metadata must
                // fail without changing either source, including this footer.
                if (header[HeaderPage.P_FILE_VERSION] >= HeaderPage.MVCC_FILE_VERSION)
                {
                    var verifier = new WalChecksum();
                    var salt = new byte[16];
                    Buffer.BlockCopy(header, WalChecksum.SaltPosition, salt, 0, salt.Length);
                    verifier.Reset(salt);
                    WalRetirement.Load(new BufferSlice(header, 0, PAGE_SIZE), reader.RawStream, verifier);
                }
                if (_readOnly) return;
                this.RequireWritableStorage();

                using var structural = new StructuralScope(_signals);
                // Repair and sync the header before removing its recovery copy.
                // Legacy redo stays until checkpoint also repairs converted pages.
                if (_recoveredHeader != null)
                {
                    // Make an OS-cached recovery copy durable before repairing its primary. The primary
                    // is torn: no data sync may come first (the data barrier below proves the file),
                    // and an encrypted data writer syncs its file when it is created, so it comes after.
                    SyncLogBarrierUnproven(((ChecksummedWalStream)_writer.Value).RawStream);
                    SyncLogDirectory();
                }
                var repaired = header;
                this.UseDataWriter(data =>
                {
                    if (_recoveredHeader != null)
                    {
                        this.CrashPoint("promotion-recovery-before-header-write");
                        data.Position = 0;
                        this.CountDataWrite();
                        data.Write(repaired, 0, repaired.Length);
                        this.CrashPoint("promotion-recovery-after-header-write");
                    }
                    this.SyncDataBarrier(data);
                });
                this.CrashPoint("promotion-recovery-after-header-flush");
                if (journal.Legacy && !published) return;
                // The journal is the header's only recovery copy until a data sync covers it (#2242).
                if (!_dataBarrierSynced && !_volatileLog) throw UnsyncedHeaderRecovery();
                var writer = ((ChecksummedWalStream)_writer.Value).RawStream;
                this.ShrinkLog(writer, journal.Legacy ? 0 : WalPadding.AlignedLength(journal.Position), "a header repair");
                SyncLogBarrier(writer);
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
