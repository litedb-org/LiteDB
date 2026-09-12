using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    /// <summary>
    /// Releases remaining snapshot leases during transaction disposal. Errors
    /// are collected only on failure, without allocating delegates per page.
    /// </summary>
    internal static class TransactionPageCleanup
    {
        internal static void Release(Snapshot snapshot, MemoryCache cache, ref List<Exception> errors)
        {
            try
            {
                if (snapshot.Mode == LockMode.Write)
                {
                    var pages = snapshot.GetWritablePages(true, true)
                        .Concat(snapshot.GetWritablePages(false, true));
                    foreach (var page in pages)
                    {
                        ReleasePage(page, cache, true, ref errors);
                    }
                }
                else
                {
                    foreach (var page in snapshot.LocalPages)
                    {
                        ReleasePage(page, cache, false, ref errors);
                    }
                    if (snapshot.CollectionPage != null)
                    {
                        ReleasePage(snapshot.CollectionPage, cache, false, ref errors);
                    }
                }
            }
            catch (Exception ex)
            {
                // Invalid snapshot enumeration must not prevent other snapshots
                // or the transaction's stream reader from being cleaned up.
                errors ??= new List<Exception>();
                errors.Add(ex);
            }
        }

        private static void ReleasePage(BasePage page, MemoryCache cache, bool writable, ref List<Exception> errors)
        {
            try
            {
                var buffer = page.TakeBuffer();
                if (writable)
                {
                    cache.DiscardPage(buffer);
                }
                else
                {
                    buffer.Release();
                }
            }
            catch (Exception ex)
            {
                errors ??= new List<Exception>();
                errors.Add(ex);
            }
        }
    }
}
