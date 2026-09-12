using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class RuntimeLock_Tests
    {
        [Fact]
        public async Task CollectionLock_Times_Out_While_Held_And_Can_Be_Reentered_After_Release()
        {
            var collectionLock = new CollectionLock();
            using (var acquired = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var holder = Task.Run(() =>
                {
                    collectionLock.TryEnter(TimeSpan.Zero).Should().BeTrue();
                    acquired.Set();
                    release.Wait();
                    collectionLock.Exit();
                });

                try
                {
                    acquired.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

                    collectionLock.TryEnter(TimeSpan.FromMilliseconds(100)).Should().BeFalse();
                }
                finally
                {
                    release.Set();
                    await holder;
                }
            }

            collectionLock.TryEnter(TimeSpan.FromSeconds(1)).Should().BeTrue();
            collectionLock.Exit();
        }

        [Fact]
        public async Task MemoryCache_Extends_Once_Per_Concurrent_Request()
        {
            const int pageCount = 32;
            const int workerCount = 8;
            var cache = new MemoryCache(new[] { 1 });
            var pages = new ConcurrentBag<PageBuffer>();
            using (var ready = new CountdownEvent(workerCount))
            using (var start = new ManualResetEventSlim())
            {
                var workers = Enumerable.Range(0, workerCount)
                    .Select(_ => Task.Factory.StartNew(() =>
                    {
                        ready.Signal();
                        start.Wait();

                        for (var i = 0; i < pageCount / workerCount; i++)
                        {
                            pages.Add(cache.NewPage());
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                    .ToArray();

                try
                {
                    ready.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                }
                finally
                {
                    start.Set();
                    await Task.WhenAll(workers);
                }
            }

            pages.Should().HaveCount(pageCount);
            pages.Select(x => x.UniqueID).Should().OnlyHaveUniqueItems();
            cache.ExtendSegments.Should().Be(pageCount);
            cache.WritablePages.Should().Be(pageCount);

            foreach (var page in pages)
            {
                cache.DiscardPage(page);
            }
        }
    }
}
