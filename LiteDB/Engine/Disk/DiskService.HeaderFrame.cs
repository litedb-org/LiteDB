using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Decision 11: before the first frame of an empty WAL, write the header frame (see
        /// <see cref="HeaderFrame"/>): a copy of the data header the frames after it depend on. The
        /// batch's own log sync makes it durable with its commit. A write that fails may leave a torn
        /// frame 0, so it sets <paramref name="uncertain"/> while it runs and the caller stops the engine.
        /// Caller holds the log writer lock and is about to append a frame.
        /// </summary>
        private void WriteHeaderFrame(Stream stream, ref bool uncertain)
        {
            if (!ChecksumsEnabled || _volatileLog || _checksums.JournalBytes != 0 || Interlocked.Read(ref _logLength) != -PAGE_SIZE) return;
            var raw = ((ChecksummedWalStream)stream).RawStream;
            var frame = new byte[WalChecksum.FrameSize];
            var header = this.UseDataWriter(ReadDataHeader);
            // Only the header these frames depend on: intact, with the salt they carry.
            if (!HeaderFrame.IsIntact(header) || header[HeaderPage.P_FILE_VERSION] < HeaderPage.CHECKSUM_FILE_VERSION)
                throw new PageChecksumException(FileOrigin.Data, 0);
            for (var i = 0; i < _checksums.Salt.Length; i++)
                if (header[WalChecksum.SaltPosition + i] != _checksums.Salt[i]) throw new PageChecksumException(FileOrigin.Data, 0);
            Buffer.BlockCopy(header, 0, frame, 0, PAGE_SIZE);
            WalChecksum.PrepareHeaderFrame(frame, _checksums.Salt);
            this.CrashPoint("header-frame-before-write");
            uncertain = true;
            Interlocked.Exchange(ref _logLength, 0);
            try
            {
                raw.Position = 0;
                raw.Write(frame, 0, frame.Length);
            }
            catch (Exception failure)
            {
                // As for a failed append (WriteLogPage): truncate what the write may have left, and the
                // engine goes on; if that fails too, the torn frame stays and the caller stops the engine.
                Interlocked.Exchange(ref _logLength, -PAGE_SIZE);
                try
                {
                    stream.SetLength(0);
                    _logFactory.TrimCapacity(stream);
                    uncertain = false;
                }
                catch (Exception cleanup)
                {
                    LOG($"truncating a failed header frame write failed too: {cleanup.Message}", "ERROR");
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
                throw;
            }
            uncertain = false;
            this.CrashPoint("header-frame-after-write");
        }

        /// <summary>
        /// Open, decision 11: the data file is empty or shorter than its header page, beside a WAL
        /// that starts with a header frame. Such a database is never initialized over (that would
        /// discard every frame): restore its header from the header frame, when the data file held
        /// nothing else when the WAL started, or refuse the open, changing neither file. A WAL without
        /// a header frame holds no commit of this version, and the database is new as before.
        /// </summary>
        private bool RestoreDataFileFromLog(long dataLength, bool encrypted)
        {
            if (!_logFactory.Exists()) return false;
            // A caller's stream exists, also when empty.
            var exists = !(_dataFactory is FileStreamFactory) || _dataFactory.Exists();
            // A read-only open of a missing file reports the path error as before.
            if (_readOnly && !exists) return false;
            var header = this.ReadLogHeaderFrame();
            var state = !exists ? "missing" : dataLength == 0 ? "empty" : "shorter than its header page";
            if (header == null)
            {
                // No readable header frame (torn, never written back, or an encrypted log opened without
                // its password), but frames of a WAL: never initialize over them either.
                if (this.LogHoldsFrames(encrypted))
                    throw new LiteException(LiteException.INVALID_DATABASE, $"Cannot open this database: its data file is {state}, " +
                        "while its log file holds WAL frames this open cannot read without the data file's header (the log's " +
                        "header frame is missing or damaged, or the log is encrypted and needs the password). Restore the " +
                        "data file or open it with its password, or move the log file aside to create a new database.");
                return false;
            }
            if (exists && dataLength > 0 && !HeaderFrame.Completes(this.ReadDataPrefix(dataLength), header)) return false;
            if (!exists || !HeaderFrame.Fits(header, 0))
                throw new LiteException(LiteException.INVALID_DATABASE, $"Cannot open this database: its data file is {state}, " +
                    "while its log file holds the WAL of a database whose pages were in that data file. Restore the data " +
                    "file, or move the log file aside to create a new database.");
            if (_readOnly)
                throw new LiteException(LiteException.INVALID_DATABASE, $"Cannot open this database read-only: its data file " +
                    $"is {state} (a power loss before the data file synced), and its log file holds the data header and " +
                    "every commit. Open it once without read-only to restore the data file's header from the log file.");
            this.RestoreDataHeader(header);
            return true;
        }

        /// <summary>
        /// Open, decision 11: the data header is neither an intact checksummed nor a legacy header
        /// (see <see cref="HeaderFrame.IsIntact"/>), no header journal restored it, and the WAL's
        /// header frame holds one whose pages the data file still has. Use it: a writable open writes
        /// it back, a read-only one reads it in place of the data file's.
        /// </summary>
        private void RestoreHeaderFromLog(ref byte[] header, long dataLength)
        {
            if (_recoveredHeader != null || HeaderFrame.IsIntact(header)) return;
            var frame = this.ReadLogHeaderFrame();
            if (frame == null || !HeaderFrame.Completes(header, frame) || !HeaderFrame.Fits(frame, dataLength)) return;
            header = frame;
            if (_readOnly)
            {
                _recoveredHeader = frame;
                LOG("reading the database header from the log file's header frame", "RECOVERY");
            }
            else this.RestoreDataHeader(frame);
        }

        private void RestoreDataHeader(byte[] header)
        {
            this.RequireWritableStorage();
            using var structural = new StructuralScope(_signals);
            LOG("restoring the database header from the log file's header frame", "RECOVERY");
            // The header frame stays: the WAL is emptied only after a data sync covered this write (ShrinkLog).
            this.UseDataWriter(data =>
            {
                data.Position = 0;
                this.CountDataWrite();
                data.Write(header, 0, PAGE_SIZE);
                this.SyncDataBarrier(data);
            });
        }

        /// <summary>
        /// Whether the log holds a WAL frame (checked by its own trailer, <see cref="WalChecksum.IsFrame"/>),
        /// or, opened without a password, starts like an encrypted file. A log with neither holds no commit.
        /// </summary>
        private bool LogHoldsFrames(bool encrypted)
        {
            if (!_logFactory.Exists()) return false;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try
            {
                var raw = reader.RawStream;
                var frame = new byte[WalChecksum.FrameSize];
                if (!encrypted && LooksEncrypted(raw)) return true;
                for (long position = 0; (position / PAGE_SIZE + 1) * WalChecksum.FrameSize <= raw.Length; position += PAGE_SIZE)
                {
                    raw.Position = position / PAGE_SIZE * WalChecksum.FrameSize;
                    raw.ReadRequired(frame, 0, frame.Length);
                    if (WalChecksum.IsFrame(frame, position)) return true;
                }
                return false;
            }
            finally { _logPool.Return(reader); }
        }

        /// <summary>
        /// An encryption preamble, read without the password: the marker 1, the salt, zeros, the
        /// password check at <see cref="AesPreamble.CHECK_START"/>, and zeros to the end of the page. A
        /// legacy (v5) WAL page starts with its page ID and does not look like this.
        /// </summary>
        private static bool LooksEncrypted(Stream raw)
        {
            if (raw.Length < PAGE_SIZE) return false;
            var page = new byte[PAGE_SIZE];
            raw.Position = 0;
            raw.ReadRequired(page, 0, PAGE_SIZE);
            const int saltEnd = 1 + ENCRYPTION_SALT_SIZE, checkEnd = AesPreamble.CHECK_START + AesPreamble.CHECK_SIZE;
            if (page[0] != 1) return false;
            for (var i = saltEnd; i < AesPreamble.CHECK_START; i++) if (page[i] != 0) return false;
            for (var i = checkEnd; i < PAGE_SIZE; i++) if (page[i] != 0) return false;
            for (var i = AesPreamble.CHECK_START; i < checkEnd; i++) if (page[i] != 0) return true;
            return false;
        }

        private byte[] ReadDataPrefix(long dataLength)
        {
            var bytes = new byte[dataLength];
            var stream = _dataPool.Rent();
            try
            {
                stream.Position = 0;
                stream.ReadRequired(bytes, 0, bytes.Length);
            }
            finally { _dataPool.Return(stream); }
            return bytes;
        }

        private byte[] ReadLogHeaderFrame()
        {
            if (!_logFactory.Exists()) return null;
            var reader = (ChecksummedWalStream)_logPool.Rent();
            try { return HeaderFrame.Read(reader.RawStream); }
            finally { _logPool.Return(reader); }
        }
    }
}
