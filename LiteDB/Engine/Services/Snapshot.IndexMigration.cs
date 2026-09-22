namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        // Migration has no concurrent cursors. Keep empty pages in this index's
        // free list until its replacement keys are built, without making deleted
        // pages globally reusable inside an ordinary transaction.
        internal bool RetainEmptyIndexPages { get; set; }

        internal void ReleaseEmptyIndexPages(CollectionIndex index)
        {
            RetainEmptyIndexPages = false;
            var next = index.FreeIndexPageList;
            ulong count = 0;
            while (next != uint.MaxValue)
            {
                Constants.ENSURE(count++ < MaxItemsCount, "Loop in migration free index pages");
                var page = this.GetPage<IndexPage>(next);
                next = page.NextPageID;
                if (page.ItemsCount == 0)
                {
                    this.RemoveFreeList(page, ref index.FreeIndexPageList);
                    this.DeletePage(page);
                }
                this.Safepoint();
            }
        }
    }
}
