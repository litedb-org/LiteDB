using System;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Bounded hints for pages already pinned by another reader. Only the cache
    /// monitor can change an idle page to busy or make the final release idle.
    /// Additional readers can share that existing lifetime with atomic counts.
    /// </summary>
    internal sealed class SharedPageReads
    {
        // 32 KiB of references on a 64-bit host, independent of database size.
        private readonly PageBuffer[] _pages = new PageBuffer[4096];
#if TESTING
        internal Action AfterHintRead { get; set; }
#endif

        internal PageBuffer TryPin(long position, FileOrigin origin)
        {
            var page = Volatile.Read(ref _pages[this.Slot(position, origin)]);
            if (page == null) return null;
            var readers = Volatile.Read(ref page.ShareCounter);
#if TESTING
            AfterHintRead?.Invoke();
#endif
            while (readers > 0)
            {
                if (Interlocked.CompareExchange(ref page.ShareCounter, readers + 1, readers) == readers)
                {
                    // The hint may have been evicted and reused before the CAS.
                    // Validate identity AFTER acquiring a pin. A positive count
                    // now prevents eviction, reuse, and segment reclamation.
                    if (page.Position == position && page.Origin == origin)
                    {
                        Volatile.Write(ref page.Referenced, 1);
                        return page;
                    }
                    page.Release();
                    return null;
                }
                readers = Volatile.Read(ref page.ShareCounter);
            }
            return null;
        }

        internal static bool TryReleaseShared(PageBuffer page)
        {
            var readers = Volatile.Read(ref page.ShareCounter);
            while (readers > 1)
            {
                if (Interlocked.CompareExchange(ref page.ShareCounter, readers - 1, readers) == readers) return true;
                readers = Volatile.Read(ref page.ShareCounter);
            }
            return false;
        }

        // Publication/removal run under the existing cache monitor. Removal
        // prevents hints from retaining buffers after their segments are freed.
        internal void Remember(PageBuffer page) => Volatile.Write(ref _pages[this.Slot(page.Position, page.Origin)], page);

        internal void Forget(PageBuffer page) => Interlocked.CompareExchange(
            ref _pages[this.Slot(page.Position, page.Origin)], null, page);

        internal void Clear() => Array.Clear(_pages, 0, _pages.Length);

        private int Slot(long position, FileOrigin origin) =>
            ((int)(position / PAGE_SIZE) ^ ((int)origin * 997)) & (_pages.Length - 1);
    }
}
