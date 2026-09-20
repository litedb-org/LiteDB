using System;
using System.IO;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private WalIdentity _walIdentity;
        private bool _walIdentityEnabled;
        private bool _ignoreCompletedWal;

        // Transactional WALs use a binding prefix. DiskService also supports raw
        // page I/O, whose frames need not be transaction records.
        internal void EnableWalIdentity() => _walIdentityEnabled = true;

        private void RejectOrphanWal()
        {
            if (!_logFactory.Exists() || _logFactory.GetLength() == 0) return;
            var log = _logPool.Rent();
            try
            {
                // An encrypted file can contain only its encryption header.
                if (log.Length > 0) throw WalIdentity.Invalid("Cannot create a database beside an existing WAL.");
            }
            finally { _logPool.Return(log); }
        }

        private void ValidateWal(PageBuffer header)
        {
            _walIdentity = WalIdentity.Read(header);
            if (!_logFactory.Exists() || _logFactory.GetLength() == 0) return;
            var log = _logPool.Rent();
            try
            {
                _ignoreCompletedWal = WalIdentity.Validate(header, log) == WalIdentity.Replay.Completed;
            }
            finally
            {
                _logPool.Return(log);
            }
            if (_ignoreCompletedWal && !_readOnly) this.TruncateWal();
        }

        private void EnsureWalPrefix(Stream log)
        {
            if (!_walIdentityEnabled) return;
            if (_readOnly) throw new NotSupportedException("Cannot write a read-only database.");
            if (this.GetFileLength(FileOrigin.Log) != 0) return;
            if (_walIdentity == null) this.PersistWalIdentity(WalIdentity.Create());

            var data = _dataPool.Writer.Value;
            PageBuffer prefix;
            lock (data) prefix = WalIdentity.ReadHeader(data);
            _walIdentity.Write(prefix);
            // Old engines ignore this unconfirmed transaction and start IDs at 1.
            prefix.Write(0u, BasePage.P_TRANSACTION_ID);
            prefix.Write(false, BasePage.P_IS_CONFIRMED);
            try
            {
                log.Position = 0;
                log.Write(prefix.Array, prefix.Offset, PAGE_SIZE);
                Interlocked.Exchange(ref _logLength, 0);
            }
            catch
            {
                try { log.SetLength(0); } catch { }
                throw;
            }
        }

        private void PersistWalIdentity(WalIdentity identity)
        {
            try
            {
                var data = _dataPool.Writer.Value;
                lock (data)
                {
                    var header = WalIdentity.ReadHeader(data);
                    identity.Write(header);
                    data.Position = 0;
                    data.Write(header.Array, header.Offset, PAGE_SIZE);
                    FlushWalMetadata(data);
                    _walIdentity = identity;
                }
            }
            catch (Exception ex) when (!(ex is IOException))
            {
                throw new IOException("WAL identity persistence failed.", ex);
            }
        }

        internal void PrepareCheckpointCompletion()
        {
            // Data pages have already reached stable storage. An old WAL can now
            // be identified as completed even if the process exits before truncate.
            if (_walIdentityEnabled && _walIdentity != null)
            {
                // Rolled-back safepoints may leave an unconfirmed tail which
                // only received ordinary Flush. Every byte in the fingerprint
                // must be durable before publishing the completed generation.
                this.FlushWalLog(_logPool.Writer.Value);
                var log = _logPool.Rent();
                try { this.PersistWalIdentity(_walIdentity.Next(log)); }
                finally { _logPool.Return(log); }
            }
        }

        internal void FinishCheckpointCompletion()
        {
            // Legacy WALs have no prefix: truncate them durably before publishing
            // protection, so an interrupted migration remains a valid legacy file.
            if (_walIdentityEnabled && _walIdentity == null)
                this.PersistWalIdentity(WalIdentity.Create());
        }

        internal void TruncateWal()
        {
            try
            {
                this.SetLength(0, FileOrigin.Log);
                this.FlushWalLog(_logPool.Writer.Value);
            }
            catch (Exception ex) when (!(ex is IOException))
            {
                throw new IOException("WAL checkpoint truncation failed.", ex);
            }
        }

        /// <summary>
        /// Log storage that cannot sync at all (#2242) degrades to an OS-cache flush, exactly as commits do (#2818).
        /// </summary>
        private void FlushWalLog(Stream log)
        {
            try
            {
                lock (log) this.FlushLogToDisk(log);
            }
            catch (Exception ex) when (!(ex is IOException))
            {
                throw new IOException("WAL identity/checkpoint durable flush failed.", ex);
            }
        }

        private static void FlushWalMetadata(Stream stream)
        {
            try { stream.FlushToDisk(); }
            catch (Exception ex) when (!(ex is IOException))
            {
                throw new IOException("WAL identity/checkpoint durable flush failed.", ex);
            }
        }
    }
}
