using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class FileReaderV8
    {
        /// <summary>
        /// Load log file to build index map (wal map index)
        /// </summary>
        private void LoadIndexMap()
        {
            var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, 0);
            var transactions = new Dictionary<uint, List<PagePosition>>();
            var confirmedTransactions = new List<uint>();
            var currentPosition = 0L;
            var pageInfo = new PageInfo { Origin = FileOrigin.Log };

            _logStream.Position = 0;

            while (_logStream.Position < _logStream.Length)
            {
                try
                {
                    _logStream.Position = pageInfo.Position = currentPosition;

                    var read = _logStream.ReadFully(buffer.Array, buffer.Offset, PAGE_SIZE);

                    if (buffer.IsBlank())
                    {
                        // this should not happen, but if it does, it means there's a zeroed page in the file
                        // just skip it
                        currentPosition += PAGE_SIZE;
                        continue;
                    }

                    var pageID = buffer.ReadUInt32(BasePage.P_PAGE_ID);
                    var isConfirmed = buffer.ReadBool(BasePage.P_IS_CONFIRMED);
                    var transactionID = buffer.ReadUInt32(BasePage.P_TRANSACTION_ID);

                    pageInfo.PageID = pageID;
                    pageInfo.ColID = buffer.ReadUInt32(BasePage.P_COL_ID);

                    ENSURE(read == PAGE_SIZE, "Page position {0} read only than {1} bytes (instead {2})", _logStream, read, PAGE_SIZE);

                    var position = new PagePosition(pageID, currentPosition);

                    if (transactions.TryGetValue(transactionID, out var list))
                    {
                        list.Add(position);
                    }
                    else
                    {
                        transactions[transactionID] = new List<PagePosition> { position };
                    }

                    // when page confirm transaction, add to confirmed transaction list
                    if (isConfirmed)
                    {
                        confirmedTransactions.Add(transactionID);
                    }
                }
                catch (Exception ex)
                {
                    this.HandleError(ex, pageInfo);
                }
                finally
                {
                    currentPosition += PAGE_SIZE;
                }
            }

            // now, log index map using only confirmed transactions (override with last transactionID)
            foreach (var transactionID in confirmedTransactions)
            {
                var mapIndexPages = transactions[transactionID];

                // update
                foreach (var page in mapIndexPages)
                {
                    _logIndexMap[page.PageID] = page.Position;
                }
            }
        }

    }
}
