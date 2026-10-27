using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [CollectionDefinition("SharedReaderCleanup", DisableParallelization = true)]
    public sealed class SharedReaderCleanupCollection { }

    [Collection("SharedReaderCleanup")]
    public class SharedReaderCleanup_Tests
    {
        [Fact]
        public void Blocked_abandoned_cleanup_does_not_block_finalizers_and_keeps_resources_rooted()
        {
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            using var timedOut = new ManualResetEventSlim();
            var disposalThread = 0;
            var reader = new BlockingReader(entered, resume, timedOut);
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
            timedOut.IsSet.Should().BeFalse("a cleanup safety timeout must fail the test instead of silently unblocking disposal");
            disposalThread.Should().NotBe(0);
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
        private static WeakReference Abandon(IBsonDataReader reader, Action release)
        {
            var result = new SharedDataReader(reader, release, ownsResources: true);
            return new WeakReference(result);
        }

        private static void CollectPromptly()
        {
            using var done = new ManualResetEventSlim();
            var collector = new Thread(() => { Collect(); done.Set(); }) { IsBackground = true };
            collector.Start();
            done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("blocked disposal must not run on the finalizer thread");
            collector.Join();
        }

        internal static void Collect()
        {
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
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
