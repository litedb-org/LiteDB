using System;
using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Tracks private reservations under the header monitor. The live allocator's
    /// header excludes them from reuse; each persisted header includes other
    /// owners' reservations as ordinary empty pages, never as missing/live pages.
    /// </summary>
    internal sealed class PageAllocationService
    {
        private readonly HashSet<TransactionPages> _pending = new HashSet<TransactionPages>();
        private Dictionary<uint, uint> _publishedLinks;
        private List<KeyValuePair<uint, uint>> _publishing;

        // Caller holds the header monitor. Unpublished reservations of another
        // collection may not have a readable physical image yet.
        internal HashSet<uint> GetOtherReservations(TransactionPages owner)
        {
            var pages = new HashSet<uint>();
            foreach (var pending in _pending)
                if (pending != owner) pages.UnionWith(pending.NewPages);
            return pages;
        }

        internal void Reserve(TransactionPages owner, uint pageID)
        {
            if (owner.NewPages.Count == 0) _pending.Add(owner);
            owner.NewPages.Add(pageID);
        }

        internal void Complete(TransactionPages owner)
        {
            _pending.Remove(owner);
            if (_publishedLinks == null) return;
            // Cache only confirmed empty images. A failed publication cannot make
            // a later commit rely on a placeholder that never became durable.
            foreach (var link in _publishing) _publishedLinks[link.Key] = link.Value;
            _publishing.Clear();
            foreach (var pageID in owner.NewPages) _publishedLinks.Remove(pageID);
            if (_pending.Count == 0)
            {
                _publishedLinks = null;
                _publishing = null;
            }
        }

        /// <summary>
        /// Emit a complete free-list image and its confirming header in the same
        /// WAL transaction. A crash frees abandoned reservations by ordinary WAL
        /// replay. A still-active owner retains its own private buffer/WAL image
        /// and replaces these empty images only in its eventual commit.
        /// </summary>
        internal IEnumerable<PageBuffer> CommitHeader(HeaderPage header, TransactionPages owner,
            uint transactionID, DiskService disk)
        {
            var free = header.FreeEmptyPageList;
            foreach (var pending in _pending)
            {
                if (pending == owner) continue;
                foreach (var pageID in pending.NewPages)
                {
                    // Unchanged links already have a confirmed empty image. Do
                    // not rewrite an idle writer's reservations on every commit.
                    if (_publishedLinks == null || !_publishedLinks.TryGetValue(pageID, out var next) || next != free)
                    {
                        _publishedLinks ??= new Dictionary<uint, uint>();
                        _publishing ??= new List<KeyValuePair<uint, uint>>();
                        _publishing.Add(new KeyValuePair<uint, uint>(pageID, free));
                        var page = new BasePage(disk.NewPage(), pageID, PageType.Empty)
                        {
                            NextPageID = free,
                            TransactionID = transactionID
                        };
                        yield return page.UpdateBuffer();
                    }
                    free = pageID;
                }
            }

            // Only this clone gets the recovery free list. In-process allocation
            // must continue to exclude every other active owner's reservations.
            var buffer = header.UpdateBuffer();
            var clone = disk.NewPage();
            Buffer.BlockCopy(buffer.Array, buffer.Offset, clone.Array, clone.Offset, PAGE_SIZE);
            clone.Write(free, HeaderPage.P_FREE_EMPTY_PAGE_ID);
            yield return clone;
        }
    }
}
