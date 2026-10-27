using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using LiteDB.Utils;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(nameof(SharedAbandonedReaderCollection))]
    public class SharedReaderCleanup_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Owned_cleanup_releases_the_handle_and_callback_once_even_when_disposal_throws(bool readerFails, bool callbackFails)
        {
            var reader = new ThrowingReader(readerFails);
            var calls = 0;
            var cleanup = new SharedReaderCleanup(reader, () =>
            {
                calls++;
                if (callbackFails) throw new InvalidOperationException("release failed");
            });
            Action dispose = cleanup.Dispose;
            if (readerFails || callbackFails) dispose.Should().Throw<InvalidOperationException>();
            else dispose.Should().NotThrow();
            cleanup.Dispose();
            reader.Disposals.Should().Be(1);
            calls.Should().Be(1);
            typeof(SharedReaderCleanup).GetField("_root", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(cleanup).As<GCHandle>().IsAllocated.Should().BeFalse();
        }

        [Fact]
        public void Blocked_abandoned_cleanup_does_not_block_finalizers_and_keeps_resources_rooted()
        {
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            var disposalThread = 0;
            var reader = new BlockingReader(entered, resume);
            var resources = new WeakReference(reader);
            var weak = Abandon(reader, () =>
            {
                disposalThread = Environment.CurrentManagedThreadId;
                released.Set();
            });
            reader = null;
            try
            {
                CollectPromptly();
                weak.IsAlive.Should().BeFalse();
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the normal-thread reaper must enter disposal");
                CollectPromptly();
                resources.IsAlive.Should().BeTrue("the GC handle must root the resource graph until disposal completes");
                released.IsSet.Should().BeFalse();
            }
            finally { resume.Set(); }
            released.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            disposalThread.Should().NotBe(0);
            Reachability.Snapshot()["maintenance:shared-abandoned-reader-cleanup"].Should().BeGreaterThan(0);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(IBsonDataReader reader, Action release)
        {
            var result = new SharedDataReader(reader, release, ownsResources: true);
            return new WeakReference(result);
        }

        private static void CollectPromptly()
        {
            using var done = new ManualResetEventSlim();
            var collector = new Thread(() => { SharedAbandonedReader_Tests.Collect(); done.Set(); }) { IsBackground = true };
            collector.Start();
            done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("blocked disposal must not run on the finalizer thread");
            collector.Join();
        }

        private sealed class BlockingReader : IBsonDataReader
        {
            private readonly ManualResetEventSlim _entered;
            private readonly ManualResetEventSlim _resume;
            internal BlockingReader(ManualResetEventSlim entered, ManualResetEventSlim resume)
            { _entered = entered; _resume = resume; }
            public BsonValue this[string field] => BsonValue.Null;
            public string Collection => "docs";
            public BsonValue Current => BsonValue.Null;
            public bool HasValues => false;
            public bool Read() => false;
            public void Dispose() { _entered.Set(); _resume.Wait(TimeSpan.FromSeconds(10)); }
        }

        private sealed class ThrowingReader : IBsonDataReader
        {
            private readonly bool _fails;
            internal int Disposals;
            internal ThrowingReader(bool fails) => _fails = fails;
            public BsonValue this[string field] => BsonValue.Null;
            public string Collection => "docs";
            public BsonValue Current => BsonValue.Null;
            public bool HasValues => false;
            public bool Read() => false;
            public void Dispose()
            {
                Disposals++;
                if (_fails) throw new InvalidOperationException("reader failed");
            }
        }
    }
}
