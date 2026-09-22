using System;
using System.IO;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private bool _hasHeaderPromotion;
        private byte[] _readOnlyPromotionHeader;

        private void JournalHeaderPromotion(byte[] header)
        {
            var log = _writer.Value;
            lock (log)
            {
                var position = _logLength + PAGE_SIZE;
                var pages = HeaderPromotionJournal.Encode(header, position);
                this.CrashPoint("promotion-before-journal-write");
                log.Position = position;
                foreach (var page in pages)
                {
                    if (log is AesStream encrypted) encrypted.WriteHeaderPromotionPage(page);
                    else log.Write(page, 0, PAGE_SIZE);
                    this.CrashPoint("promotion-after-journal-page-write");
                }
                // Promotion must never use the optional/degraded commit-flush policy:
                // the recovery image must be durable before touching the data header.
                this.SyncHeaderPromotionJournal(log);
                Interlocked.Add(ref _logLength, pages.Length * PAGE_SIZE);
                _hasHeaderPromotion = true;
                this.CrashPoint("promotion-after-journal-flush");
            }
        }

        private void RecoverHeaderPromotion()
        {
            if (!_logFactory.Exists() || _logFactory.GetLength() < 2L * PAGE_SIZE) return;
            var log = _logPool.Rent();
            byte[] recovered = null;
            try
            {
                var previous = new byte[PAGE_SIZE];
                var current = new byte[PAGE_SIZE];
                var length = _logFactory.GetLength();
                log.Position = 0;
                for (long position = 0; position + PAGE_SIZE <= length; position += PAGE_SIZE)
                {
                    if (log is AesStream encrypted) encrypted.ReadHeaderPromotionPage(current);
                    else log.ReadRequired(current, 0, PAGE_SIZE);
                    if (position >= PAGE_SIZE && HeaderPromotionJournal.IsPart(current, position - PAGE_SIZE, 1))
                    {
                        recovered = HeaderPromotionJournal.Decode(previous, current, position - PAGE_SIZE) ?? recovered;
                    }
                    var swap = previous;
                    previous = current;
                    current = swap;
                }
            }
            finally { _logPool.Return(log); }

            if (recovered == null) return;
            _hasHeaderPromotion = true;
            var reader = _dataPool.Rent();
            try
            {
                var currentHeader = new byte[PAGE_SIZE];
                reader.Position = 0;
                reader.ReadRequired(currentHeader, 0, PAGE_SIZE);
                // Error-close can set this guard directly after promotion. It is not
                // restored by ordinary WAL replay; repairing the version must not
                // suppress a separately requested automatic rebuild.
                if (currentHeader[HeaderPage.P_INVALID_DATAFILE_STATE] == 1)
                    recovered[HeaderPage.P_INVALID_DATAFILE_STATE] = 1;
            }
            finally { _dataPool.Return(reader); }
            if (_readOnly)
            {
                // Replay uses the same base image without repairing a read-only file.
                _readOnlyPromotionHeader = recovered;
                return;
            }
            var data = _dataPool.Writer.Value;
            // A process crash may have left a complete record in the OS cache only.
            // Re-sync it before repair, just as before the original promotion.
            this.SyncHeaderPromotionJournal(_writer.Value);
            this.CrashPoint("promotion-recovery-before-header-write");
            data.Position = 0;
            data.Write(recovered, 0, recovered.Length);
            this.CrashPoint("promotion-recovery-after-header-write");
            data.FlushToDisk();
            this.CrashPoint("promotion-recovery-after-header-flush");
            // Keep the journal until checkpoint: a second crash during this repair
            // must remain recoverable. Normal WAL replay restores newer committed headers.
        }

        private void RetireHeaderPromotion(Stream log)
        {
            if (!_hasHeaderPromotion) return;
            this.CrashPoint("promotion-before-journal-retire-flush");
            // Persist truncation before any WAL slots can be reused. Otherwise an old
            // recovery image could survive alongside a new WAL epoch after power loss.
            try { log.FlushToDisk(); }
            catch (Exception ex)
            {
                // Until truncation is known durable, reusing these slots could expose
                // an old recovery image mixed with a new WAL epoch after power loss.
                var failure = new IOException("Header promotion journal retirement failed; reopen the database.", ex);
                _state.Handle(failure);
                throw failure;
            }
            _hasHeaderPromotion = false;
            this.CrashPoint("promotion-after-journal-retire-flush");
        }

        private void SyncHeaderPromotionJournal(Stream log)
        {
            log.FlushToDisk();
            if (_logFactory is FileStreamFactory file) file.SyncDirectory();
        }
    }
}
