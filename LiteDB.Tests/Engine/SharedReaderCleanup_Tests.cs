using System;
using System.IO;
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
            using var file = new TempFile();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            using var timedOut = new ManualResetEventSlim();
            var disposalThread = 0;
            var weak = AbandonBlocked(file.Filename, entered, resume, timedOut, () =>
            {
                disposalThread = Environment.CurrentManagedThreadId;
                released.Set();
            }, out var resources);
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
            timedOut.IsSet.Should().BeFalse("a cleanup safety timeout must fail the test instead of silently unblocking disposal");
            disposalThread.Should().NotBe(0);
            Reachability.Snapshot()["maintenance:shared-abandoned-reader-cleanup"].Should().BeGreaterThan(0);
            SpinWait.SpinUntil(() =>
            {
                CollectPromptly();
                return !resources.IsAlive;
            }, TimeSpan.FromSeconds(5)).Should().BeTrue("completed cleanup must release its resource root");
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Abandoned_cleanup_contains_each_owners_disposal_exceptions(bool readerFails, bool callbackFails)
        {
            var reader = new ThrowingReader(readerFails);
            var calls = 0;
            var cleanup = new SharedReaderCleanup(reader, () =>
            {
                Interlocked.Increment(ref calls);
                if (callbackFails) throw new InvalidOperationException("release failed");
            });
            cleanup.Abandon();
            SharedReaderCleanup.Drain();
            var root = typeof(SharedReaderCleanup).GetField("_root", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            SpinWait.SpinUntil(() => !((GCHandle)root.GetValue(cleanup)).IsAllocated, TimeSpan.FromSeconds(5))
                .Should().BeTrue("the worker must free each handle even if disposal throws");
            cleanup.Dispose();
            reader.Disposals.Should().Be(1);
            Volatile.Read(ref calls).Should().Be(1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference AbandonBlocked(string filename, ManualResetEventSlim entered,
            ManualResetEventSlim resume, ManualResetEventSlim timedOut, Action release, out WeakReference resources)
        {
            // Custom readers are deliberately excluded from the callback-free root.
            // Observe a lease FileStream actually held by that root instead of the
            // BlockingReader receiver, whose liveness during Dispose is JIT-dependent.
            // Keep allocation in this frame so caller temporaries cannot root it.
            var leaseFiles = new[] { new FileStream(filename, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose) };
            resources = new WeakReference(leaseFiles[0]);
            var result = new SharedDataReader(new BlockingReader(entered, resume, timedOut), release,
                ownsResources: true, leaseFiles: leaseFiles);
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
            private readonly ManualResetEventSlim _timedOut;
            internal BlockingReader(ManualResetEventSlim entered, ManualResetEventSlim resume, ManualResetEventSlim timedOut)
            { _entered = entered; _resume = resume; _timedOut = timedOut; }
            public BsonValue this[string field] => BsonValue.Null;
            public string Collection => "docs";
            public BsonValue Current => BsonValue.Null;
            public bool HasValues => false;
            public bool Read() => false;
            public void Dispose()
            {
                _entered.Set();
                if (!_resume.Wait(TimeSpan.FromSeconds(60)))
                {
                    _timedOut.Set();
                    throw new TimeoutException("The test did not resume blocked disposal.");
                }
            }
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
