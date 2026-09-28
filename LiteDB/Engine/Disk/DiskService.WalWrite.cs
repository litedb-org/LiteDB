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
                    // an existing slot), or at its end when the truncation that removes a failed append
                    // failed too or had to keep a header journal. Recovery stops at the first invalid
                    // frame. Whatever the exception type, stop before releasing the writer: no later
                    // commit may be appended (and acknowledged) behind it.
                    flushFailure = ex as IOException ?? new IOException("WAL frame write failed.", ex);
                    this.RecordWriteFailure("A WAL write", WriteFailure.InFile(flushFailure, FileOrigin.Log));
                    ownsFailure = _state.BeginStop(flushFailure);
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
                        this.RecordWriteFailure("A commit's log flush", WriteFailure.InFile(flushFailure, FileOrigin.Log));
                        ownsFailure = _state.BeginStop(flushFailure);
                    }
                }
                else if (flushFailure == null) stream.Flush();
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
                // From here a failure can leave part of the frame on the stream. A caller stream that
                // buffers holds nothing once the write returned (ConcurrentStream flushes it).
                overwrite = page.Position < previousStreamLength.Value;
                uncertain = true;
                stream.Write(page.Array, page.Offset, PAGE_SIZE);
                uncertain = false;
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
                            // The truncation removes a torn append, not a torn overwrite of an earlier slot.
                            if (!overwrite) uncertain = false;
                        }
                        catch (Exception cleanup)
                        {
                            // The torn frame stays: report the write failure that left it, not its cleanup.
                            LOG($"truncating a failed WAL write failed too: {cleanup.Message}", "ERROR");
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
    }
}
