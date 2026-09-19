using System;
using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal sealed partial class MemoryCache
    {
        public PageBuffer GetWritablePage(long position, FileOrigin origin, Action<long, BufferSlice> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            var key = this.GetReadableKey(position, origin);
            PageBuffer writable = null;
            PageBuffer readable = null;
            try
            {
                lock (_sync)
                {
                    this.ThrowIfDisposedLocked();
                    while (_index.TryGetValue(key, out var loading) && loading.State == FrameState.Loading)
                    {
                        Monitor.Wait(_sync);
                        this.ThrowIfDisposedLocked();
                    }
                    if (_index.TryGetValue(key, out readable))
                    {
                        if (readable.ShareCounter == 0)
                        {
                            var reused = readable;
                            readable = null;
                            return this.TakeIdleWritableLocked(key, reused);
                        }
                        // This pin protects the source during eviction and copying.
                        this.PinLocked(readable);
                        Interlocked.Increment(ref _hits);
                    }
                    else _misses++;
                    writable = this.AcquireWritableLocked(position, origin);
                }
                if (readable != null)
                {
#if TESTING
                    BeforeWritableCopy?.Invoke();
#endif
                    Buffer.BlockCopy(readable.Array, readable.Offset, writable.Array, writable.Offset, PAGE_SIZE);
                }
                else
                {
                    writable.Clear();
                    factory(position, writable);
                }
                return writable;
            }
            catch
            {
                if (writable != null) this.DiscardPage(writable);
                throw;
            }
            finally
            {
                readable?.Release();
            }
        }

        private PageBuffer AcquireWritableLocked(long position, FileOrigin origin)
        {
            var page = _reclaimer.AcquireFrameLocked();
            page.Position = position;
            page.Origin = origin;
            page.State = FrameState.Writable;
            page.ShareCounter = BUFFER_WRITABLE;
            page.Referenced = 0;
            _pool.ChangeBusyLocked(page.Segment, 1);
            _writablePages++;
            return page;
        }

        private PageBuffer TakeIdleWritableLocked(long key, PageBuffer page)
        {
            // No reader can pin an idle frame without this lock. Detach the
            // committed cache entry before allowing its bytes to be modified;
            // subsequent readers load the unchanged version from disk.
            ENSURE(page.State == FrameState.Readable && page.ShareCounter == 0, "only idle readable pages can become writable");
            _index.Remove(key);
            _sharedReads.Forget(page);
            _readablePages--;
            _idleReadablePages--;
            _writablePages++;
            _pool.ChangeBusyLocked(page.Segment, 1);
            page.State = FrameState.Writable;
            page.ShareCounter = BUFFER_WRITABLE;
            page.Referenced = 0;
            page.Generation++;
            page.RefreshOwnerGeneration();
            Interlocked.Increment(ref _hits);
            return page;
        }
    }
}
