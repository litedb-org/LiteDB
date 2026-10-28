using System.Collections.Generic;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        // The legacy node format has no index owner. Inspect physical collection
        // pages, including full pages absent from every index's free list.
        internal List<uint> GetVectorPages(long pageBudget = long.MaxValue)
        {
            uint last;
            HashSet<uint> privatePages;
            lock (_header)
            {
                last = _header.LastPageID;
                privatePages = _header.Allocations.GetOtherReservations(_transPages);
            }
            var physicalPages = (ulong)(_disk.GetFileLength(FileOrigin.Data) / PAGE_SIZE)
                + (ulong)(_disk.GetFileLength(FileOrigin.Log) / PAGE_SIZE);
            ENSURE(last < uint.MaxValue && (ulong)last <= physicalPages + (ulong)_transPages.NewPages.Count + (ulong)privatePages.Count,
                "Invalid page bound during vector ownership scan");
            if (last > pageBudget) return null;
            var dataPages = _disk.GetFileLength(FileOrigin.Data) / PAGE_SIZE;
            var pages = new List<uint>();
            for (uint id = 1; id <= last; id++)
            {
                if (id == _collectionPage.PageID || (_mode == LockMode.Write && privatePages.Contains(id))) continue;
                if (_localPages.TryGetValue(id, out var local))
                {
                    if (local.PageType == PageType.VectorIndex && local.ColID == _collectionPage.PageID)
                        pages.Add(id);
                }
                else
                {
                    // A writer holds this collection's lock, but foreign pages
                    // can be committed or reused after its WAL pin. Classify their
                    // current images; readers must retain their pinned ownership.
                    var latest = _mode == LockMode.Write;
                    if (id >= dataPages && !(latest && _transPages.DirtyPages.ContainsKey(id)) &&
                        _walIndex.GetPageIndex(id, latest ? int.MaxValue : _readVersion, out _) == long.MaxValue)
                    {
                        // The global bound includes pages invisible to an older
                        // reader. There is no data-file image to fall back to.
                        ENSURE(!latest, $"Missing committed page {id} during vector ownership scan");
                        continue;
                    }
                    var page = this.ReadPage<BasePage>(id, out _, out _, out _, latest);
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
