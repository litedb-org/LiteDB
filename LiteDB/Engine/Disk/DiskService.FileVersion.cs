using System;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        internal byte FileVersion { get; set; } = HeaderPage.FILE_VERSION;

        /// <summary>
        /// Publish the vector format before any vector bytes can enter the WAL.
        /// </summary>
        internal void PromoteVectorFormat() => this.PromoteFileFormat(HeaderPage.VECTOR_FILE_VERSION);

        /// <summary>
        /// Writes hold the header lock and an active transaction; startup migration
        /// owns the disk exclusively. Only the persisted header is copied, never
        /// uncommitted header fields. Flush before publishing dependent WAL pages.
        /// A checkpoint (<paramref name="checkpointStops"/>) holds the WAL writer until it stopped
        /// the engine after any failure, so the promotion leaves the stop to it.
        /// </summary>
        internal void PromoteFileFormat(byte version, bool checkpointStops = false)
        {
            if (FileVersion >= version) return;
            if (!ChecksumsEnabled) throw new InvalidOperationException("Enable checksums before promoting index storage.");
            var writer = _writer.Value;
            _signals?.StructuralBegin();
            try
            {
                this.WriteFileVersion(writer, version, checkpointStops);
            }
            finally
            {
                _signals?.StructuralEnd(-1);
            }
        }

        /// <summary>
        /// The promotion keeps its header journal (the log's recovery copy of the header) until a
        /// data sync covered the new header, like a checkpoint (<see cref="KeepsWal"/>): it writes only
        /// to a data file that just synced, is refused unchanged while the data file cannot sync, and
        /// stops the engine with the journal kept when the data file stops syncing in between.
        /// </summary>
        internal static IOException UnsyncedPromotion()
        {
            var error = new IOException("Cannot upgrade this database's file format now: its data file cannot sync " +
                "to the device, and the upgrade keeps its header's recovery copy in the log file until the new header " +
                "synced. Retry once the storage syncs.");
            error.Data[UnsyncedPromotionDataKey] = true;
            error.Data[RefusedBeforeWriteDataKey] = true;
            return UnsyncedStorage(error);
        }

        /// <summary>Exception.Data key of <see cref="UnsyncedPromotion"/>: refused before anything was written.</summary>
        internal const string UnsyncedPromotionDataKey = "LiteDB.UnsyncedPromotion";

        /// <summary>
        /// Exception.Data key: refused because the data file cannot sync (#2242) before anything was
        /// written. Not a failure (implementation note 6 of docs/decisions/durability-policy.md, in both
        /// modes): nothing is recorded, the engine keeps writing, and the operation's caller gets it. So
        /// is an overwrite refused before it wrote because the log cannot sync, without durable commits
        /// (<see cref="IsQuietOverwriteRefusal"/>).
        /// </summary>
        internal const string RefusedBeforeWriteDataKey = "LiteDB.RefusedBeforeWrite";

        /// <summary>See <see cref="RefusedBeforeWriteDataKey"/>.</summary>
        internal static bool IsRefusedBeforeWrite(Exception error) =>
            error.Data.Contains(RefusedBeforeWriteDataKey) && IsUnsyncedStorage(error);

        private void WriteFileVersion(Stream writer, byte version, bool checkpointStops)
        {
            Exception failure = null;
            var ownsFailure = false;
            lock (writer)
            {
                this.UseDataWriter(stream =>
                {
                    try
                    {
                        var header = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
                        stream.Position = 0;
                        stream.ReadRequired(header.Array, 0, PAGE_SIZE);
                        PageChecksum.Validate(header, 0);
                        _ = new HeaderPage(header);
                        var rawLog = ((ChecksummedWalStream)writer).RawStream;
                        var originalLength = rawLog.Length;
                        if (!_volatileLog && !this.DataFileSyncs()) throw UnsyncedPromotion();
                        var compact = version >= HeaderPage.COMPACT_FILE_VERSION;
                        if (compact) this.CrashPoint("promotion-before-journal-write");
                        // The journal and its barrier go to the log file (decision 6 records the file).
                        try { BeginHeaderJournal(header.Array, promotion: compact); }
                        catch (Exception ex) when (FailedIn(ex, FileOrigin.Log)) { }
                        if (compact) this.CrashPoint("promotion-after-journal-flush");
                        header[HeaderPage.P_FILE_VERSION] = version;
                        if (version == HeaderPage.MVCC_FILE_VERSION) new WalRetirement().WriteHeader(header);
                        PageChecksum.Write(header);
                        stream.Position = 0;
                        if (compact) this.CrashPoint("promotion-before-header-write");
                        this.CountDataWrite();
                        try { stream.Write(header.Array, 0, PAGE_SIZE); }
                        catch (Exception ex) when (FailedIn(ex, FileOrigin.Data)) { }
                        if (compact) this.CrashPoint("promotion-after-header-write");
                        this.SyncDataBarrier(stream);
                        if (compact) this.CrashPoint("promotion-after-header-flush");
                        // Only once a data sync covered the new header (ShrinkLog): otherwise stop below.
                        this.ShrinkLog(rawLog, originalLength, "a file format promotion");
                        if (compact) this.CrashPoint("promotion-before-journal-retire-flush");
                        SyncLogBarrier(rawLog);
                        if (compact) this.CrashPoint("promotion-after-journal-retire-flush");
                        _checksums.JournalBytes = 0;
                        FileVersion = version;
                    }
                    // A refusal before this promotion wrote anything (an earlier journal still outstanding)
                    // tore nothing: it is no failure (implementation note 6).
                    catch (Exception ex) when (_checksums.JournalBytes != 0 && !checkpointStops && !IsRefusedBeforeWrite(ex))
                    {
                        // The journal is the only recovery copy of a header this write may have torn.
                        // Whatever the exception type, stop before releasing the writer: a rollback or
                        // another write must not append to (or truncate) the WAL behind it. The next
                        // open restores the header from the journal.
                        failure = ex as IOException ?? new IOException("File format promotion failed.", ex);
                        // The record names the file of the write or sync that failed, whatever wraps it.
                        if (ex.Data[WriteFailure.FileDataKey] is string file) failure.Data[WriteFailure.FileDataKey] = file;
                        this.RecordWriteFailure("A file format promotion", failure);
                        ownsFailure = _state.BeginStop(failure);
                    }
                });
            }
            if (failure != null)
            {
                _state.CompleteStop(failure, ownsFailure);
                throw failure;
            }
        }

        private void PreserveFileVersion(PageBuffer page)
        {
            // Both write paths own this writable/uncached page; no shared read frame is modified.
            if (page.ReadUInt32(BasePage.P_PAGE_ID) == 0 && page.ReadByte(BasePage.P_PAGE_TYPE) == (byte)PageType.Header)
            {
                page[HeaderPage.P_FILE_VERSION] = Math.Max(page[HeaderPage.P_FILE_VERSION], FileVersion);
            }
        }
    }
}
