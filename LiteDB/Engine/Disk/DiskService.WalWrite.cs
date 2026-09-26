using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            var stream = _writer.Value;
            Exception flushFailure = null;
            var ownsFailure = false;

            // do a global write lock - only 1 thread can write on disk at time
            lock (stream)
            {
                _state.Validate();
                using (var iterator = pages.GetEnumerator())
                {
                    if (!iterator.MoveNext()) return 0;

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
                                ref count, ref hasConfirmation);

                            if (first && rebase)
                            {
                                // Old frames may already be durable from a safepoint.
                                // Make their new-ID tail anchor equally durable first.
                                this.FlushLogToDisk(stream);
                                this.RewriteLogTransactionIDs(stream, previousPositions,
                                    transactionState.TransactionID);
                            }

                            first = false;
                        }
                        while (iterator.MoveNext());

                        if (delayedConfirmation != null)
                        {
                            this.WriteLogPage(stream, delayedConfirmation, written,
                                transactionPages, ref transactionAnchored, false,
                                ref count, ref hasConfirmation);
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

                // A confirmation makes this WAL batch recoverable. Make all preceding
                // pages durable before WAL-index confirmation or acknowledging commit.
                if (hasConfirmation)
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
                        ownsFailure = _state.BeginStop(flushFailure);
                    }
                }
                else stream.Flush();
            }

            if (flushFailure != null)
            {
                _state.CompleteStop(flushFailure, ownsFailure);
                throw flushFailure;
            }

            return count;
        }

        private void WriteLogPage(Stream stream, PageBuffer page, Action<uint, long> written,
            IReadOnlyDictionary<uint, PagePosition> transactionPages, ref bool transactionAnchored,
            bool forceAppend, ref int count, ref bool hasConfirmation)
        {
            var previousLogLength = _logLength;
            long? previousStreamLength = null;
            PageBuffer readable = null;

            try
            {
                ENSURE(page.ShareCounter == BUFFER_WRITABLE, "to enqueue page, page must be writable");
                previousStreamLength = stream.Length;
                var pageID = page.ReadUInt32(BasePage.P_PAGE_ID);
                var isConfirmed = page.ReadBool(BasePage.P_IS_CONFIRMED);

                // Only this transaction can see its unconfirmed slots. Keep the
                // confirmation page last so recovery sees every page.
                if (!forceAppend && !isConfirmed && transactionPages != null &&
                    transactionPages.TryGetValue(pageID, out var previous) && _checksums.CanReuse(previous.Position))
                {
                    page.Position = previous.Position;
                    _cache.Invalidate(page.Position, FileOrigin.Log);
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
                this.RecordLogPosition(pageID, page.Position);
                this.RecordLogTransactionID(page.ReadUInt32(BasePage.P_TRANSACTION_ID));
                page.Origin = FileOrigin.Log;
                stream.Position = page.Position;

#if DEBUG || TESTING
                _state.SimulateDiskWriteFail?.Invoke(page);
#endif

                this.CrashPoint(isConfirmed ? "wal-confirmation-before-write" : "wal-page-before-write");
                this.PreserveFileVersion(page);
                stream.Write(page.Array, page.Offset, PAGE_SIZE);
                this.CrashPoint(isConfirmed ? "wal-confirmation-after-write" : "wal-page-after-write");
                hasConfirmation |= isConfirmed;

                // Publish only after the bytes are written to the stream.
                // The callback can make the position visible to readers.
                readable = _cache.MoveToReadable(page);
                written?.Invoke(pageID, readable.Position);

                transactionAnchored = true;
                count++;
            }
            catch
            {
                // The producer transferred ownership before yielding.
                // Recycle failed frames and undo unpublished reservations.
                if (readable == null && page.State == FrameState.Writable)
                {
                    _cache.DiscardPage(page);
                    Interlocked.Exchange(ref _logLength, previousLogLength);
                    if (previousStreamLength.HasValue)
                    {
                        stream.SetLength(previousStreamLength.Value);
                        _logFactory.TrimCapacity(stream);
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
