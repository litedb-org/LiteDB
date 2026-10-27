#if NET8_0_OR_GREATER
using System;
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
    public class SharedReaderCleanupMappedCycles_Tests
    {
        [OneFileTheory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Callback_request_cycle_releases_pages_generation_facade_and_native_participant(bool warm, bool disposeReader)
        {
            using var files = new SharedOneFilePublicationFixture();
            SharedMemoryReaderTable table = null;
            WeakReference[] weak = null;
            Exception error = null;
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            var reaper = (Timer)typeof(SharedReaderCleanup).GetField("Reaper",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
            reaper.Change(Timeout.Infinite, Timeout.Infinite);
            try
            {
                var creator = new Thread(() =>
                {
                    try { weak = Abandon(files.Settings, warm, disposeReader, out table); }
                    catch (Exception ex) { error = ex; }
                });
                creator.Start();
                creator.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
                error.Should().BeNull();
                Thread.Sleep(1800);
                SpinWait.SpinUntil(() =>
                {
                    SharedReaderCleanupCycles_Tests.Collect();
                    return !weak[0].IsAlive;
                }, TimeSpan.FromSeconds(5)).Should().BeTrue("the process table and cleanup root must not own the request's reader");
                if (!disposeReader)
                {
                    table.HasReaders.Should().BeTrue("registration finalization must not precede deferred cursor disposal");
                    SharedReaderCleanupCycles_Tests.Collect();
                    Volatile.Read(ref finalized).Should().Be(0);
                }
                reaper.Change(20, 20);
                SpinWait.SpinUntil(() => !table.HasReaders && table.Closed, TimeSpan.FromSeconds(10))
                    .Should().BeTrue("both the result generation and abandoned facade must release their native participant");
                SharedReaderCleanupCycles_Tests.Collect();
                foreach (var reference in weak) reference.IsAlive.Should().BeFalse();
                Volatile.Read(ref finalized).Should().Be(0);
                using (var peer = new SharedEngine(files.Settings))
                using (var db = new LiteDatabase(peer, disposeOnClose: false))
                {
                    Verify(db, 0);
                    db.GetCollection("other").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "peer" });
                    db.Checkpoint();
                }
                using var reopened = new LiteDatabase(files.Filename);
                Verify(reopened, 0);
                reopened.GetCollection("other").FindById(1)["value"].AsString.Should().Be("peer");
                files.WriteIsFree();
                Volatile.Read(ref finalized).Should().Be(0);
            }
            finally { reaper.Change(20, 20); SharedReaderCleanupCycles_Tests.Collect(); PageBuffer.FinalizedInUse = null; }
        }

        [OneFileFact]
        public void Process_table_preserves_transform_identity_checks_and_live_transform_behavior()
        {
            using var files = new SharedOneFilePublicationFixture();
            var request = new Request();
            var settings = files.Settings;
            settings.ReadTransform = request.Transform;
            using var first = new SharedEngine(settings);
            var same = files.Settings;
            same.ReadTransform = settings.ReadTransform;
            using var second = new SharedEngine(same);
            for (var i = 0; i < 3; i++)
            {
                using var previous = second.Query("docs", new Query { Limit = 1 });
                previous.Read().Should().BeTrue();
            }
            second.HasCachedSnapshot.Should().BeFalse("the process cache must not publish callback-bearing cores");
            request.Calls = 0;
            using var reader = second.Query("docs", Query.All());
            var count = 0;
            while (reader.Read()) { reader.Current["payload"].AsString.Length.Should().Be(1024); count++; }
            count.Should().Be(160);
            request.Calls.Should().Be(160);
            Action different = () => new SharedEngine(files.Settings).Dispose();
            different.Should().Throw<InvalidOperationException>();
            var other = files.Settings;
            other.ReadTransform = new Request().Transform;
            Action changed = () => new SharedEngine(other).Dispose();
            changed.Should().Throw<InvalidOperationException>();
        }

        [OneFileFact]
        public void Closed_waits_for_native_teardown_after_the_final_reference_is_removed()
        {
            using var files = new SharedOneFilePublicationFixture();
            SharedMemoryReaderTable table;
            using (var engine = new SharedEngine(files.Settings))
            {
                table = engine.MappedReaderTable;
                table.TryRetain().Should().BeTrue();
            }
            Exception error = null;
            var closer = new Thread(() =>
            {
                try { table.Release(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            try
            {
                lock (table.ReadCache.Gate)
                {
                    closer.Start();
                    SpinWait.SpinUntil(() => Field<int>(table, "_references") == 0,
                        TimeSpan.FromSeconds(5)).Should().BeTrue();
                    table.Closed.Should().BeFalse("final native teardown is blocked on the cache gate");
                    Action incompatible = () => new LiteDatabase(files.Filename).Dispose();
                    incompatible.Should().Throw<System.IO.IOException>();
                }
            }
            finally { closer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); }
            error.Should().BeNull();
            table.Closed.Should().BeTrue();
            using var reopened = new LiteDatabase(files.Filename);
            Verify(reopened, 0);
        }

        [OneFileFact]
        public void Collected_transform_identity_cannot_be_replaced_while_participant_is_retained()
        {
            using var files = new SharedOneFilePublicationFixture();
            var table = RetainParticipantWithoutCallback(files, out var weak);
            try
            {
                SpinWait.SpinUntil(() =>
                {
                    SharedReaderCleanupCycles_Tests.Collect();
                    return !weak.IsAlive;
                }, TimeSpan.FromSeconds(5)).Should().BeTrue("the participant must not root the original delegate");
                Action withoutTransform = () => new SharedEngine(files.Settings).Dispose();
                withoutTransform.Should().Throw<InvalidOperationException>();
                var changed = files.Settings;
                changed.ReadTransform = new Request().Transform;
                Action replacement = () => new SharedEngine(changed).Dispose();
                replacement.Should().Throw<InvalidOperationException>();
            }
            finally { table.Release(); }
            table.Closed.Should().BeTrue();
            using var reopened = new SharedEngine(files.Settings);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SharedMemoryReaderTable RetainParticipantWithoutCallback(SharedOneFilePublicationFixture files, out WeakReference weak)
        {
            // Create settings inside this non-inlined frame: mutating an argument
            // would let a caller's JIT temporary retain the delegate under test.
            var settings = files.Settings;
            settings.ReadTransform = new Request().Transform;
            weak = new WeakReference(settings.ReadTransform);
            using var engine = new SharedEngine(settings);
            var table = engine.MappedReaderTable;
            table.TryRetain().Should().BeTrue();
            return table;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] Abandon(EngineSettings settings, bool warm, bool disposeReader, out SharedMemoryReaderTable table)
        {
            var request = new Request();
            settings.ReadTransform = request.Transform;
            var engine = new SharedEngine(settings);
            table = engine.MappedReaderTable;
            if (warm) { using var previous = engine.Query("docs", Query.All()); previous.Read().Should().BeTrue(); }
            var reader = engine.Query("docs", Query.All());
            reader.Should().BeOfType<SharedDataReader>();
            reader.Read().Should().BeTrue();
            request.Calls.Should().BeGreaterThan(0);
            request.Reader = reader;
            if (disposeReader) reader.Dispose();
            return new[] { new WeakReference(reader), new WeakReference(engine), new WeakReference(request) };
        }

        private sealed class Request
        {
            internal IBsonDataReader Reader;
            internal int Calls;
            internal BsonValue Transform(string name, BsonValue value) { Calls++; return value; }
        }
    }
}
#endif
