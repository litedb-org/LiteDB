using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        // The legacy node format has no index owner. Inspect physical collection
        // pages, including full pages absent from every index's free list.
        internal List<uint> GetVectorPages(int pageBudget = int.MaxValue)
        {
            uint last;
            HashSet<uint> privatePages;
            lock (_header)
            {
                last = _header.LastPageID;
                privatePages = _header.Allocations.GetOtherReservations(_transPages);
            }
            ENSURE((ulong)last <= (ulong)MaxItemsCount + AdditionalTraversalItemsCount,
                "Invalid page bound during vector ownership scan");
            if (last > pageBudget) return null;
            var pages = new List<uint>();
            for (uint id = 1; id <= last; id++)
            {
                if (id == _collectionPage.PageID || privatePages.Contains(id)) continue;
                if (_localPages.TryGetValue(id, out var local))
                {
                    if (local.PageType == PageType.VectorIndex && local.ColID == _collectionPage.PageID)
                        pages.Add(id);
                }
                else
                {
                    var page = this.ReadPage<BasePage>(id, out _, out _, out _, false);
                    var owned = page.PageType == PageType.VectorIndex && page.ColID == _collectionPage.PageID;
                    // Do not put unrelated pages or untyped wrappers in this
                    // snapshot's writable cache. No borrowed view escapes here.
                    if (_mode == LockMode.Write) _disk.Cache.DiscardPage(page.Buffer);
                    else page.Buffer.Release();
                    if (owned) pages.Add(id);
                }
                this.Safepoint();
            }
            return pages;
        }

        internal void DeleteVectorPage(VectorIndexPage page)
        {
            // Every vector free-list head was detached by the caller. This is
            // collection-wide retirement, so no list links may survive either.
            var slots = new List<byte>();
            foreach (var node in page.GetNodes()) slots.Add(node.Position.Index);
            foreach (var slot in slots) page.DeleteNode(slot);
            page.PrevPageID = page.NextPageID = uint.MaxValue;
            this.DeletePage(page);
        }
    }
}
