using System;
using System.Collections.Generic;
using System.IO;
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
            var count = 0;
            var hasConfirmation = false;
            var stream = _writer.Value;
            var transactionAnchored = transactionPages != null && transactionPages.Count > 0;
            Exception flushFailure = null;
            var ownsFailure = false;

            // do a global write lock - only 1 thread can write on disk at time
            lock(stream)
            {
                _state.Validate();
                foreach (var page in pages)
                {
                    var previousLogLength = _logLength;
                    long? previousStreamLength = null;
                    PageBuffer readable = null;

                    try
                    {
                        ENSURE(page.ShareCounter == BUFFER_WRITABLE, "to enqueue page, page must be writable");
                        previousStreamLength = stream.Length;
                        var pageID = page.ReadUInt32(BasePage.P_PAGE_ID);
                        // Only this transaction can see its unconfirmed slots. Keep
                        // the confirmation page last so recovery sees every page.
                        if (!page.ReadBool(BasePage.P_IS_CONFIRMED) && transactionPages != null &&
                            transactionPages.TryGetValue(pageID, out var previous))
                        {
                            page.Position = previous.Position;
                            _cache.Invalidate(page.Position, FileOrigin.Log);
                        }
                        else
                        {
                            page.Position = this.AllocateLogPosition(pageID,
                                page.ReadBool(BasePage.P_IS_CONFIRMED), transactionAnchored);
                        }
                        this.RecordLogPosition(pageID, page.Position);
                        page.Origin = FileOrigin.Log;
                        stream.Position = page.Position;

#if DEBUG || TESTING
                        _state.SimulateDiskWriteFail?.Invoke(page);
#endif

                        this.PreserveFileVersion(page);
                        stream.Write(page.Array, page.Offset, PAGE_SIZE);
                        hasConfirmation |= page.ReadBool(BasePage.P_IS_CONFIRMED);

                        // Publish only after the bytes are written to the stream.
                        // The callback can make the position visible to readers.
                        readable = _cache.MoveToReadable(page);

                        written?.Invoke(pageID, readable.Position);

                        // AllocateLogPosition appends while this is false. Once
                        // those bytes exist, later frames of the same transaction
                        // may safely use reclaimed positions.
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
                // A confirmation makes this WAL batch recoverable. Make all preceding
                // pages durable before WAL-index confirmation or acknowledging commit.
                if (hasConfirmation)
                {
                    try
                    {
                        this.FlushConfirmedLog(stream);
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
    }
}
