using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Decision 13: a WAL batch failed, maybe after its frames reached the operating system (its caller
    /// got "outcome unknown"). What was acknowledged before it (the WAL's logical end then), and the
    /// files as the failure left them: the raw log from that end on, its salt, the data file's length,
    /// its header, and its copy of every page the failed transaction wrote (its safepoints included).
    /// Another connection or process can build on the failed commit, from the files, without changing
    /// the log's length or salt: its recovery takes the commit whole, and a partial checkpoint copies
    /// its pages into the data file. A read-only reopen replays only up to the acknowledged end while
    /// every one of these still matches; otherwise the files win, whole.
    /// </summary>
    internal sealed class AcknowledgedLog
    {
        internal long End;
        internal long RawLength;
        internal byte[] Salt;
        internal byte[] LogTail;
        internal uint[] PageIDs;
        internal long DataLength;
        internal byte[] DataPages;
    }

    internal partial class DiskService
    {
        /// <summary>The record of <see cref="AcknowledgedLog"/>, or null (the files decide). Caller holds the log writer lock.</summary>
        private AcknowledgedLog AcknowledgedLogAt(long end, IReadOnlyDictionary<uint, PagePosition> transactionPages, List<uint> batchPages)
        {
            if (!ChecksumsEnabled || _volatileLog || !_writer.IsValueCreated) return null;
            try
            {
                var pages = batchPages.Concat(transactionPages?.Keys ?? Enumerable.Empty<uint>()).Distinct().OrderBy(x => x).ToArray();
                var record = new AcknowledgedLog { End = end, Salt = (byte[])_checksums.Salt.Clone(), PageIDs = pages };
                // A reader of its own: reading through the writer would move an encrypted writer's position.
                var reader = (ChecksummedWalStream)_logPool.Rent();
                try
                {
                    record.RawLength = reader.RawStream.Length;
                    record.LogTail = HashLogTail(reader.RawStream, end);
                }
                finally { _logPool.Return(reader); }
                (record.DataLength, record.DataPages) = this.HashDataPages(pages);
                return record;
            }
            catch (Exception)
            {
                // The files cannot even be read back: the reopen shows what they hold.
                return null;
            }
        }

        /// <summary>
        /// The read-only engine that replaces a failed one, or a shared connection's later read-only
        /// engine, replays the WAL only up to what was acknowledged before the failure, while the files
        /// are exactly as the failure left them (<see cref="AcknowledgedLog"/>). A later open that
        /// nothing bounds lets the device decide, as recovery always does.
        /// </summary>
        private void BoundLogToAcknowledged(WriteFailure failure)
        {
            var record = failure?.Acknowledged;
            if (record == null || !ChecksumsEnabled || !_logFactory.Exists()) return;
            if (record.End - PAGE_SIZE >= _logLength || !_checksums.Salt.SequenceEqual(record.Salt)) return;
            try
            {
                var reader = (ChecksummedWalStream)_logPool.Rent();
                try
                {
                    if (reader.RawStream.Length != record.RawLength || !HashLogTail(reader.RawStream, record.End).SequenceEqual(record.LogTail)) return;
                }
                finally { _logPool.Return(reader); }
                var (length, pages) = this.HashDataPages(record.PageIDs);
                if (length != record.DataLength || !pages.SequenceEqual(record.DataPages)) return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return;
            }
            LOG($"replaying the log up to the last commit acknowledged before the write failure ({record.End} bytes)", "RECOVERY");
            _logLength = record.End - PAGE_SIZE;
            _logTrailingLength = 0;
        }

        /// <summary>
        /// The raw log from the acknowledged end's frame to its end, read as whole 16-byte blocks and
        /// never past the end: an encrypted reader that hit its end reads nothing more, and pooled
        /// readers are reused. A partial block at the end is covered by the length compared with it.
        /// </summary>
        private static byte[] HashLogTail(Stream raw, long end)
        {
            var from = Math.Min(end / PAGE_SIZE * WalChecksum.FrameSize, raw.Length);
            var remaining = (raw.Length - from) / 16 * 16;
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[WalChecksum.FrameSize];
                raw.Position = from;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(buffer.Length, remaining);
                    raw.ReadRequired(buffer, 0, count);
                    sha.TransformBlock(buffer, 0, count, null, 0);
                    remaining -= count;
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return sha.Hash;
            }
        }

        private (long Length, byte[] Hash) HashDataPages(uint[] pages)
        {
            var stream = _dataPool.Rent();
            try
            {
                var length = stream.Length;
                using (var sha = SHA256.Create())
                {
                    var page = new byte[PAGE_SIZE];
                    foreach (var id in new[] { 0u }.Concat(pages))
                    {
                        var position = (long)id * PAGE_SIZE;
                        if (position + PAGE_SIZE > length) continue;
                        stream.Position = position;
                        stream.ReadRequired(page, 0, PAGE_SIZE);
                        sha.TransformBlock(page, 0, PAGE_SIZE, null, 0);
                    }
                    sha.TransformFinalBlock(page, 0, 0);
                    return (length, sha.Hash);
                }
            }
            finally { _dataPool.Return(stream); }
        }
    }
}
