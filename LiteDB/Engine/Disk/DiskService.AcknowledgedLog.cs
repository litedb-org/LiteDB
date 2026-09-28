using System;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Decision 13: a WAL batch failed, maybe after its frames reached the operating system (its
        /// caller got "outcome unknown"). Mark the failure with the WAL's end before the batch and the
        /// raw log as the failure leaves it, so the read-only engine that replaces this one replays
        /// only commits acknowledged before it (<see cref="BoundLogToAcknowledged"/>).
        /// Caller holds the log writer lock.
        /// </summary>
        private T WithAcknowledgedLog<T>(T error, long acknowledgedEnd) where T : Exception
        {
            if (!ChecksumsEnabled || _volatileLog || !_writer.IsValueCreated) return error;
            try
            {
                error.Data[WriteFailure.AcknowledgedRawKey] = ((ChecksummedWalStream)_writer.Value).RawStream.Length;
                error.Data[WriteFailure.AcknowledgedSaltKey] = (byte[])_checksums.Salt.Clone();
                error.Data[WriteFailure.AcknowledgedEndKey] = acknowledgedEnd;
            }
            catch (Exception)
            {
                // The log cannot even report its length: the files decide at the reopen.
            }
            return error;
        }

        /// <summary>
        /// A read-only reopen after a failed WAL batch (decision 13) replays the WAL only up to the end
        /// acknowledged before the failure, so this process never shows a transaction its caller saw
        /// fail. Only while the log is exactly as the failure left it (same salt, same raw length):
        /// if anything wrote to it since (another process committing on top of the failed batch), the
        /// files win. A later writable open lets the device decide, as recovery always does.
        /// </summary>
        private void BoundLogToAcknowledged(WriteFailure failure)
        {
            if (failure == null || failure.AcknowledgedLogEnd < 0 || !ChecksumsEnabled || !_logFactory.Exists()) return;
            if (failure.AcknowledgedLogEnd - PAGE_SIZE >= _logLength || !_checksums.Salt.SequenceEqual(failure.FailedSalt)) return;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try { if (reader.RawStream.Length != failure.FailedRawLogLength) return; }
            finally { _logPool.Return(reader); }
            LOG($"replaying the log up to the last commit acknowledged before the write failure ({failure.AcknowledgedLogEnd} bytes)", "RECOVERY");
            _logLength = failure.AcknowledgedLogEnd - PAGE_SIZE;
            _logTrailingLength = 0;
        }
    }
}
