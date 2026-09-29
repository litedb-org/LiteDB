using System;
using System.Collections.Generic;
using System.Linq;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class TransactionService
    {
        /// <summary>
        /// Rollback transaction operation - ignore all modified pages and return new pages into disk
        /// After rollback, all snapshot are closed
        /// </summary>
        public void Rollback()
        {
            ENSURE(_state == TransactionState.Active, "transaction must be active to rollback (current state: {0})", _state);

            LOG($"rollback transaction ({_transPages.TransactionSize} pages with {_transPages.NewPages.Count} returns)", "TRANSACTION");

            // if transaction contains new pages, must return to database in another transaction
            if (_transPages.NewPages.Count > 0)
            {
                this.ReturnNewPages();
            }

            // dispose all snapshots
            foreach (var snapshot in this.Snapshots)
            {
                // but first, if writable, discard changes
                if (snapshot.Mode == LockMode.Write)
                {
                    // discard all dirty pages (only buffers still writable)
                    _disk.DiscardDirtyPages(snapshot
                        .GetWritablePages(true, true)
                        .Select(x => x.TakeBuffer())
                        .Where(x => x.ShareCounter == BUFFER_WRITABLE));

                    // discard all clean pages (only buffers still writable)
                    _disk.DiscardCleanPages(snapshot
                        .GetWritablePages(false, true)
                        .Select(x => x.TakeBuffer())
                        .Where(x => x.ShareCounter == BUFFER_WRITABLE));
                }

                // now, release pages
                snapshot.Dispose();
            }

            _state = TransactionState.Aborted;
            _disk.ForgetWalTransaction(_transPages.TransactionID);
        }

        /// <summary>
        /// Return added pages when occurs an rollback transaction (run this only in rollback). Create new transactionID and add into
        /// Log file all new pages as EmptyPage in a linked order - also, update SharedPage before store
        /// </summary>
        private void ReturnNewPages()
        {
            // create new transaction ID
            var transactionPages = new TransactionPages { TransactionID = _walIndex.NextTransactionID() };

            // now lock header to update LastTransactionID/FreePageList
            lock (_header)
            {
                // persist all empty pages into wal-file
                var pagePositions = new Dictionary<uint, PagePosition>();

                IEnumerable<PageBuffer> source()
                {
                    // create list of empty pages with forward link pointer
                    for (var i = 0; i < _transPages.NewPages.Count; i++)
                    {
                        var pageID = _transPages.NewPages[i];
                        var next = i < _transPages.NewPages.Count - 1 ? _transPages.NewPages[i + 1] : _header.FreeEmptyPageList;

                        var buffer = _disk.NewPage();

                        var page = new BasePage(buffer, pageID, PageType.Empty)
                        {
                            NextPageID = next,
                            TransactionID = transactionPages.TransactionID
                        };

                        yield return page.UpdateBuffer();

                    }

                    // update header page with my new transaction ID
                    _header.TransactionID = transactionPages.TransactionID;
                    _header.FreeEmptyPageList = _transPages.NewPages[0];
                    _header.IsConfirmed = true;

                    // clone header buffer
                    var buf = _header.UpdateBuffer();
                    var clone = _disk.NewPage();

                    Buffer.BlockCopy(buf.Array, buf.Offset, clone.Array, clone.Offset, clone.Count);

                    yield return clone;
                };

                // create a header save point before any change
                var safepoint = _header.Savepoint();

                try
                {
                    // write all pages (including new header)
                    _disk.WriteLogDisk(source(), (pageID, position) =>
                    {
                        pagePositions[pageID] = new PagePosition(pageID, position);
                    }, transactionPages, _walIndex.NextTransactionID);
                    _header.TransactionID = transactionPages.TransactionID;
                }
                catch
                {
                    // must revert all header content if any error occurs during header change
                    _header.Restore(safepoint);
                    throw;
                }

                // now confirm this transaction to wal
                _walIndex.ConfirmTransaction(transactionPages.TransactionID, pagePositions.Values);
            }
        }
    }
}
