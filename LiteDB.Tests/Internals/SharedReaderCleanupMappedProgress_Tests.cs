#if NET8_0_OR_GREATER
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedReaderCleanup")]
    public class SharedReaderCleanupMappedProgress_Tests
    {
        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Blocked_warm_generation_does_not_strand_another_participants_cleanup_or_final_unmap(bool closeFacades)
        {
            using var firstFiles = new SharedOneFilePublicationFixture();
            using var secondFiles = new SharedOneFilePublicationFixture();
            using var first = new SharedEngine(firstFiles.Settings);
            using var second = new SharedEngine(secondFiles.Settings);
            using var firstDb = new LiteDatabase(first, disposeOnClose: false);
            using var secondDb = new LiteDatabase(second, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(first, firstDb);
            SharedOneFileWarmReads_Tests.Warm(second, secondDb);
            var firstTable = first.MappedReaderTable;
            var secondTable = second.MappedReaderTable;
            var firstSnapshot = firstTable.ReadCache.Snapshot;
            var secondSnapshot = secondTable.ReadCache.Snapshot;
            var reaper = (Timer)StaticField("Reaper").GetValue(null);
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            reaper.Change(Timeout.Infinite, Timeout.Infinite);
            Thread drain = null;
            Exception error = null;
            try
            {
                var secondWeak = Abandon(second);
                SharedReaderCleanup_Tests.Collect();
                var firstWeak = Abandon(first);
                SharedReaderCleanup_Tests.Collect();
                firstWeak.IsAlive.Should().BeFalse();
                secondWeak.IsAlive.Should().BeFalse();
                firstSnapshot.Readers.Should().Be(1);
                secondSnapshot.Readers.Should().Be(1);
                var head = StaticField("_pending").GetValue(null);
                var next = typeof(SharedReaderCleanup).GetField("_next", BindingFlags.Instance | BindingFlags.NonPublic);
                head.Should().NotBeNull();
                next.GetValue(head).Should().NotBeNull("both finalizers must publish to the same batch");
                next.GetValue(next.GetValue(head)).Should().BeNull();
                if (closeFacades) { first.Dispose(); second.Dispose(); }

                // LIFO puts first at the head. Its cursor must be disposed before
                // ReleaseCached can acquire this gate and release the exact lease.
                lock (firstTable.ReadCache.Gate)
                {
                    drain = new Thread(() =>
                    {
                        try { SharedReaderCleanup.Drain(); }
                        catch (Exception ex) { error = ex; }
                    }) { IsBackground = true };
                    drain.Start();
                    SpinWait.SpinUntil(() => !secondTable.HasReaders && secondSnapshot.Lease == null && (!closeFacades || secondTable.Closed),
                        TimeSpan.FromSeconds(5)).Should().BeTrue("another participant must release its generation and finish final unmap independently");
                    secondSnapshot.Readers.Should().Be(0);
                    secondSnapshot.Lease.Should().BeNull();
                    firstSnapshot.Readers.Should().Be(1);
                    firstSnapshot.Lease.Should().NotBeNull();
                    firstTable.Closed.Should().BeFalse("the blocked result still owns its generation and participant");
                    SharedReaderCleanup_Tests.Collect();
                    Volatile.Read(ref finalized).Should().Be(0);
                }
            }
            finally
            {
                reaper.Change(20, 20);
                try
                {
                    if (drain != null) drain.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    SpinWait.SpinUntil(() => !firstTable.HasReaders && !secondTable.HasReaders &&
                        firstSnapshot.Lease == null && secondSnapshot.Lease == null,
                        TimeSpan.FromSeconds(5)).Should().BeTrue();
                    error.Should().BeNull();
                    first.Dispose();
                    second.Dispose();
                    SpinWait.SpinUntil(() => firstTable.Closed && secondTable.Closed,
                        TimeSpan.FromSeconds(5)).Should().BeTrue("final unmap follows cursor and generation release");
                    firstSnapshot.Readers.Should().Be(0);
                    firstSnapshot.Lease.Should().BeNull();
                    SharedReaderCleanup_Tests.Collect();
                    Volatile.Read(ref finalized).Should().Be(0);
                }
                finally { PageBuffer.FinalizedInUse = null; }
            }
            using var reopenedFirst = new LiteDatabase(firstFiles.Filename);
            using var reopenedSecond = new LiteDatabase(secondFiles.Filename);
            Verify(reopenedFirst, 0);
            Verify(reopenedSecond, 0);
        }

        private static FieldInfo StaticField(string name) =>
            typeof(SharedReaderCleanup).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(SharedEngine engine)
        {
            var hits = engine.CoordinatedReadHits;
            var reader = engine.Query("docs", new Query());
            reader.Should().BeOfType<SharedDataReader>();
            engine.CoordinatedReadHits.Should().Be(hits + 1, "this must exercise a mapped warm streamed result");
            reader.Read().Should().BeTrue();
            return new WeakReference(reader);
        }
    }
}
#endif
