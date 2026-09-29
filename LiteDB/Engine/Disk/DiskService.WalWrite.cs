using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // A caller log stream other than a MemoryStream may hold frames after their write returned
        // (a BufferedStream) and write them on at a later write, seek, length query or flush, where it
        // can tear one; a reader's access hands such a failure to the writer (HeldWrites). LiteDB's
        // own files never hold a frame back: their buffer is smaller than one.
        private readonly bool _logMayBuffer;

        /// <summary>
        /// Write all pages inside log file in a thread safe operation.
        /// Takes ownership of each yielded frame, including on failure.
        /// </summary>
        public int WriteLogDisk(IEnumerable<PageBuffer> pages, Action<uint, long> written = null,
            IReadOnlyDictionary<uint, PagePosition> transactionPages = null)
        {
            return this.WriteLogDisk(pages, written, transactionPages, null, null);
        }

        internal int WriteLogDisk(IEnumerable<PageBuffer> pages, Action<uint, long> written,
            TransactionPages transactionPages, Func<uint> nextTransactionID)
        {
            return this.WriteLogDisk(pages, written, transactionPages.DirtyPages,
                transactionPages, nextTransactionID);
        }

        private int WriteLogDisk(IEnumerable<PageBuffer> pages, Action<uint, long> written,
            IReadOnlyDictionary<uint, PagePosition> transactionPages,
            TransactionPages transactionState, Func<uint> nextTransactionID)
        {
            var count = 0;
            var hasConfirmation = false;
            var reusePublished = false;
            var stream = _writer.Value;
            Exception flushFailure = null;
            var ownsFailure = false;

            // do a global write lock - only 1 thread can write on disk at time
            lock (stream)
            {
                _state.Validate();
                try { _state.RequireNoWriteFailure(); }
                catch (IOException refused)
                {
                    refused.Data[CommitOutcomeDataKey] = NotCommittedOutcome;
                    throw;
                }
                // The end of what earlier batches acknowledged: a read-only reopen after this batch
                // failed replays only up to it (decision 13).
                var acknowledgedEnd = Interlocked.Read(ref _logLength) + PAGE_SIZE;
                var batchPages = new List<uint>();
                var uncertain = false;
                try
                {
                    using (var iterator = pages.GetEnumerator())
                    {
                        if (!iterator.MoveNext()) return 0;

                        // Before any frame: a commit that cannot be made durable fails here (decision 3).
                        // A batch without pages writes nothing and needs no proof.
                        try
                        {
                            this.RequireDurableCommit(stream);
                            this.WriteHeaderFrame(stream, ref uncertain);
                        }
                        catch
                        {
                            // The producer transferred the first page when it yielded it.
                            if (iterator.Current.State == FrameState.Writable) _cache.DiscardPage(iterator.Current);
                            throw;
                        }

                        var transactionAnchored = transactionPages != null && transactionPages.Count > 0;
                        var rebase = !ChecksumsEnabled && transactionState != null &&
                            transactionState.TransactionID < _lastWalTransactionID;
                        var previousPositions = rebase
                            ? transactionPages.Values.Select(x => x.Position).Distinct().ToArray()
                            : Array.Empty<long>();

                        if (rebase)
                        {
                            do transactionState.TransactionID = nextTransactionID();
                            while (transactionState.TransactionID <= _lastWalTransactionID);

                            // The first new-ID frame must append before old frames are
                            // rewritten, otherwise a crash could leave the high ID only
                            // in an earlier slot that legacy recovery does not observe.
                            transactionAnchored = false;
                        }

                        PageBuffer delayedConfirmation = null;
                        var first = true;

                        try
                        {
                            do
                            {
                                var page = iterator.Current;
                                if (transactionState != null)
                                {
                                    page.Write(transactionState.TransactionID, BasePage.P_TRANSACTION_ID);
                                }

                                if (first && rebase && page.ReadBool(BasePage.P_IS_CONFIRMED))
                                {
                                    delayedConfirmation = _cache.NewPage();
                                    Buffer.BlockCopy(page.Array, page.Offset, delayedConfirmation.Array,
                                        delayedConfirmation.Offset, PAGE_SIZE);
                                    page.Write(false, BasePage.P_IS_CONFIRMED);
                                }

                                batchPages.Add(page.ReadUInt32(BasePage.P_PAGE_ID));
                                this.WriteLogPage(stream, page, written, transactionPages,
                                    ref transactionAnchored, first && rebase,
                                    ref count, ref hasConfirmation, ref reusePublished, ref uncertain);

                                if (first && rebase)
                                {
                                    // Old frames may already be durable from a safepoint.
                                    // Make their new-ID tail anchor equally durable first.
                                    this.FlushLogToDisk(stream);
                                    this.RewriteLogTransactionIDs(stream, previousPositions,
                                        transactionState.TransactionID, ref reusePublished);
                                }

                                first = false;
                            }
                            while (iterator.MoveNext());

                            if (delayedConfirmation != null)
                            {
                                this.WriteLogPage(stream, delayedConfirmation, written,
                                    transactionPages, ref transactionAnchored, false,
                                    ref count, ref hasConfirmation, ref reusePublished, ref uncertain);
                                delayedConfirmation = null;
                            }
                        }
                        finally
                        {
                            if (delayedConfirmation?.State == FrameState.Writable)
                            {
                                _cache.DiscardPage(delayedConfirmation);
                            }
                        }
                    }
                }
                catch (Exception ex) when (uncertain)
                {
                    // A failed write can leave a torn frame: in the middle of the WAL (an overwrite of
                    // an existing slot), at its end when the truncation that removes a failed append
                    // failed too or had to keep a header journal, or anywhere in this batch when the
                    // stream buffers frames it already accepted. Recovery stops at the first invalid
                    // frame. Whatever the exception type, stop before releasing the writer: no later
                    // commit may be appended (and acknowledged) behind it.
                    flushFailure = ex as IOException ?? new IOException("WAL frame write failed.", ex);
                    // A frame may have reached the log whole although its write threw.
                    flushFailure.Data[CommitOutcomeDataKey] = UnknownOutcome;
                    this.RecordWriteFailure("A WAL write", WriteFailure.InFile(flushFailure, FileOrigin.Log),
                        this.AcknowledgedLogAt(acknowledgedEnd, transactionPages, batchPages));
                    ownsFailure = _state.BeginStop(flushFailure);
                }
                catch (Exception ex) when (!ex.Data.Contains(CommitOutcomeDataKey))
                {
                    // Nothing torn stayed behind (a failed append was truncated), and no confirmation of
                    // this batch was written, or its truncation was synced: the commit is not in the log.
                    // Whatever the exception type (a write's EPERM is an UnauthorizedAccessException).
                    ex.Data[CommitOutcomeDataKey] = NotCommittedOutcome;
                    throw;
                }

                // A confirmation makes this WAL batch recoverable. Make all preceding
                // pages durable before WAL-index confirmation or acknowledging commit.
                if (flushFailure == null && hasConfirmation)
                {
                    try
                    {
                        this.CrashPoint("wal-before-durable-flush");
                        this.FlushConfirmedLog(stream);
                        this.CrashPoint("wal-after-durable-flush");
                    }
                    catch (Exception ex)
                    {
                        // The confirmation may already be durable. Stop the engine;
                        // rollback or further writes cannot resolve this uncertainty.
                        // Publish the failure before releasing the writer lock, but
                        // defer teardown: cleanup can need the WAL-index lock while a
                        // partial checkpoint owns it and waits for this monitor.
                        flushFailure = ex as IOException ?? new IOException("WAL durable flush failed.", ex);
                        flushFailure.Data[CommitOutcomeDataKey] = UnknownOutcome;
                        this.RecordWriteFailure("A commit's log flush", WriteFailure.InFile(flushFailure, FileOrigin.Log),
                            this.AcknowledgedLogAt(acknowledgedEnd, transactionPages, batchPages));
                        ownsFailure = _state.BeginStop(flushFailure);
                    }
                }
                else if (flushFailure == null)
                {
                    try
                    {
                        stream.Flush();
                    }
                    catch (Exception ex) when (uncertain)
                    {
                        // A buffering stream writes this batch's frames on now and can tear one.
                        flushFailure = ex as IOException ?? new IOException("WAL write flush failed.", ex);
                        flushFailure.Data[CommitOutcomeDataKey] = UnknownOutcome;
                        this.RecordWriteFailure("A WAL write", WriteFailure.InFile(flushFailure, FileOrigin.Log),
                            this.AcknowledgedLogAt(acknowledgedEnd, transactionPages, batchPages));
                        ownsFailure = _state.BeginStop(flushFailure);
                    }
                }
            }

            if (flushFailure != null)
            {
#if DEBUG || TESTING
                _state.AfterFailedWalWrite?.Invoke();
#endif
                _state.CompleteStop(flushFailure, ownsFailure);
                throw flushFailure;
            }

            return count;
        }

        private void WriteLogPage(Stream stream, PageBuffer page, Action<uint, long> written,
            IReadOnlyDictionary<uint, PagePosition> transactionPages, ref bool transactionAnchored,
            bool forceAppend, ref int count, ref bool hasConfirmation, ref bool reusePublished,
            ref bool uncertain)
        {
            var previousLogLength = _logLength;
            long? previousStreamLength = null;
            var overwrite = false;
            // A buffering stream may still hold (and tear) a frame this batch wrote before this one.
            var earlierHeld = uncertain;
            // This frame is a commit's confirmation and its write began.
            var confirmationWritten = false;
            PageBuffer readable = null;

            try
            {
                ENSURE(page.ShareCounter == BUFFER_WRITABLE, "to enqueue page, page must be writable");
                previousStreamLength = stream.Length;
                var pageID = page.ReadUInt32(BasePage.P_PAGE_ID);
                var isConfirmed = page.ReadBool(BasePage.P_IS_CONFIRMED);

                // Only this transaction can see its unconfirmed slots. Keep the
                // confirmation page last so recovery sees every page. A snapshot of
                // this transaction (e.g. $dump) can still hold the old version:
                // append then, and the old frame stays part of the transaction.
                if (!forceAppend && !isConfirmed && transactionPages != null &&
                    transactionPages.TryGetValue(pageID, out var previous) && _checksums.CanReuse(previous.Position) &&
                    _cache.TryInvalidate(previous.Position, FileOrigin.Log))
                {
                    page.Position = previous.Position;
                }
                else
                {
                    // Recovery selects a transaction's last physical occurrence
                    // of a page. A checkpoint may add earlier free slots between
                    // safepoints, but a rewritten page must stay after its own
                    // earlier occurrence (other transactions may reuse freely).
                    var minimum = transactionPages != null && transactionPages.TryGetValue(pageID, out var prior)
                        ? prior.Position + PAGE_SIZE : 0;
                    page.Position = this.AllocateLogPosition(pageID, isConfirmed, transactionAnchored, minimum);
                }
                // Invalidate before the first destructive write in this locked batch.
                // Cached snapshots cannot be installed while Shared ownership is held.
                if (page.Position < previousStreamLength.Value) this.PublishWalReuse(ref reusePublished);
                this.RecordLogPosition(pageID, page.Position);
                this.RecordLogTransactionID(page.ReadUInt32(BasePage.P_TRANSACTION_ID));
                page.Origin = FileOrigin.Log;
                stream.Position = page.Position;

#if DEBUG || TESTING
                _state.SimulateDiskWriteFail?.Invoke(page);
#endif

                this.CrashPoint(isConfirmed ? "wal-confirmation-before-write" : "wal-page-before-write");
                this.PreserveFileVersion(page);
                // From here a failure can leave part of the frame on the stream. A buffering stream
                // can still tear it after the write returned, until the batch's final flush.
                overwrite = page.Position < previousStreamLength.Value;
                uncertain = true;
                confirmationWritten = isConfirmed;
                stream.Write(page.Array, page.Offset, PAGE_SIZE);
                if (!_logMayBuffer) uncertain = false;
                this.CrashPoint(isConfirmed ? "wal-confirmation-after-write" : "wal-page-after-write");
                hasConfirmation |= isConfirmed;

                // Publish only after the bytes are written to the stream.
                // The callback can make the position visible to readers.
                readable = _cache.MoveToReadable(page);
                written?.Invoke(pageID, readable.Position);

                transactionAnchored = true;
                count++;
            }
            catch (Exception failure)
            {
                // A confirmation whose write began may be in the log (it always appends): whole although
                // its write threw, or written before a later step failed. Only its synced truncation
                // below proves it is not.
                if (confirmationWritten) uncertain = true;

                // The producer transferred ownership before yielding.
                // Recycle failed frames and undo unpublished reservations.
                if (readable == null && page.State == FrameState.Writable)
                {
                    _cache.DiscardPage(page);
                    Interlocked.Exchange(ref _logLength, previousLogLength);
                    // The stream length excludes an outstanding header journal: never truncate it away.
                    if (previousStreamLength.HasValue && _checksums.JournalBytes == 0)
                    {
                        try
                        {
                            this.PublishWalReuse(ref reusePublished);
                            stream.SetLength(previousStreamLength.Value);
                            _logFactory.TrimCapacity(stream);
                            // The truncation removes a torn append, not a torn overwrite of an earlier slot
                            // nor an earlier frame of this batch that a buffering stream still held. A
                            // confirmation can reach the device whole although its write threw, and only a
                            // sync makes its truncation durable (see SyncTruncatedConfirmation).
                            if (!overwrite && !earlierHeld && (!confirmationWritten || this.SyncTruncatedConfirmation())) uncertain = false;
                        }
                        catch (Exception cleanup)
                        {
                            // The torn frame stays, or its removal may not be durable: report the write
                            // failure that left it, not its cleanup.
                            LOG($"removing a failed WAL write failed too: {cleanup.Message}", "ERROR");
                            ExceptionDispatchInfo.Capture(failure).Throw();
                        }
                    }
                }

                throw;
            }
            finally
            {
                readable?.Release();
            }
        }

        /// <summary>
        /// A commit's confirmation whose write threw was truncated away. It may still have reached the
        /// device whole, and a truncation is not durable before the log syncs: a power loss until then
        /// can leave the commit in the log for a later open to recover. So sync the log (on this failure
        /// path only, in both commit modes): true once the sync succeeded, or for a log in memory, and the
        /// commit is not in the log ("NotCommitted", decision 14). False when the log answers "cannot
        /// sync" without durable commits, or its syncs cannot report failure (<see cref="LogSyncUnverified"/>:
        /// such a sync proves nothing); with durable commits "cannot sync", and any other failure, throw.
        /// The commit's outcome is then unknown and the batch stops the engine. Caller holds the log writer lock.
        /// </summary>
        private bool SyncTruncatedConfirmation()
        {
            if (_volatileLog) return true;
            this.SyncRawLog();
            return _logBarrierSynced && !this.LogSyncUnverified;
        }
    }
}
