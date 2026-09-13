using System;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        // Called while holding the header lock so validation and consumption are atomic.
        private bool TryAllocateFreePage(out uint pageID, out PageBuffer buffer)
        {
            pageID = 0;
            buffer = null;

            // try get page from Empty free list
            if (_header.FreeEmptyPageList != uint.MaxValue &&
                _header.FreeEmptyPageList <= _header.LastPageID)
            {
                var free = this.GetPage<BasePage>(_header.FreeEmptyPageList, useLatestVersion: true);

                if (free.PageType == PageType.Empty)
                {
                    // Consume one verified page at a time. This keeps a valid
                    // prefix reusable without scanning the whole list on open.
                    _header.FreeEmptyPageList = free.NextPageID;
                    free.NextPageID = uint.MaxValue;
                    pageID = free.PageID;
                    buffer = free.Buffer;
                }
                else
                {
                    // Legacy databases can point the tail at a live page.
                    // Drop only the invalid suffix; never reuse that page.
                    _header.FreeEmptyPageList = uint.MaxValue;
                }
            }

            if (_header.FreeEmptyPageList > _header.LastPageID &&
                _header.FreeEmptyPageList != uint.MaxValue)
            {
                _header.FreeEmptyPageList = uint.MaxValue;
            }

            return pageID != 0;
        }
    }
}
