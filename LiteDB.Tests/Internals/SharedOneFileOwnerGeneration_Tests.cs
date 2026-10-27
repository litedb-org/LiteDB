#if NET8_0_OR_GREATER
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileOwnerGeneration_Tests
    {
        [OneFileFact]
        public void Old_local_lease_result_and_Stop_cannot_erase_a_successor_on_a_new_actual_Thread()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var peer = new SharedEngine(files.Settings);
            using var oldResult = peer.Query("docs", new Query());
            oldResult.Read().Should().BeTrue();
            engine.BeginTrans().Should().BeTrue();
            var oldCore = Core(engine);
            var oldState = State(oldCore);
            var oldLocal = Field<SharedLocalTransactionOwner.Lease>(engine, "_localTransaction");
            engine.Update("docs", new[] { Row(1, 9) });
            engine.Rollback().Should().BeTrue();
            var retired = engine.Pin;
            retired.Should().NotBeNull();
            retired.RequestRelease(false); retired.WaitReleased();
            oldState.Disposed.Should().BeTrue("a stale Stop must target a retired core, not the core the successor reuses");
            Exception failure = null;
            var successor = new Thread(() => failure = Record.Exception(() =>
            {
                engine.BeginTrans().Should().BeTrue();
                var current = Field<SharedLocalTransactionOwner.Lease>(engine, "_localTransaction");
                current.Thread.Should().BeSameAs(Thread.CurrentThread);
                current.Generation.Should().BeGreaterThan(oldLocal.Generation);
                engine.Update("docs", new[] { Row(1, 1) });
                var cleanup = new Thread(() =>
                {
                    oldLocal.Dispose(); oldResult.Dispose();
                    oldState.CompleteStop(new System.IO.IOException("stale Stop"), ownsFailure: true);
                });
                cleanup.Start(); cleanup.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                Action foreign = () => peer.Rollback();
                foreign.Should().Throw<InvalidOperationException>().WithMessage("*connection*");
                engine.Commit().Should().BeTrue();
            })) { IsBackground = true };
            successor.Start(); successor.Join(TimeSpan.FromSeconds(5)).Should().BeTrue(); failure.Should().BeNull();
            // Sequential migration back to the original Thread publishes another generation.
            engine.BeginTrans().Should().BeTrue(); engine.Rollback().Should().BeTrue();
            engine.Dispose(); peer.Dispose();
            for (var repeat = 0; repeat < 2; repeat++) { using var cold = new LiteDatabase(files.Filename); Verify(cold, 1); }
        }

        [OneFileFact]
        public void An_already_queued_local_waiter_wakes_after_an_ordinary_owner_Thread_exits()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var first = new SharedEngine(files.Settings);
            using var second = new SharedEngine(files.Settings);
            using var owned = new ManualResetEventSlim();
            using var exit = new ManualResetEventSlim();
            Exception ownerFailure = null, peerFailure = null;
            var owner = new Thread(() => ownerFailure = Record.Exception(() =>
            {
                first.BeginTrans().Should().BeTrue(); first.Update("docs", new[] { Row(1, 99) });
                owned.Set(); exit.Wait(TimeSpan.FromSeconds(10));
            })) { IsBackground = true };
            var peer = new Thread(() => peerFailure = Record.Exception(() =>
            {
                second.BeginTrans().Should().BeTrue(); second.Update("docs", new[] { Row(1, 1) });
                second.Commit().Should().BeTrue();
            })) { IsBackground = true };
            owner.Start();
            try
            {
                owned.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(); peer.Start();
                SpinWait.SpinUntil(() => (peer.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                Field<int>(second, "_mutexWaiters").Should().Be(0);
            }
            finally
            {
                exit.Set(); owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                peer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            ownerFailure.Should().BeNull(); peerFailure.Should().BeNull();
            first.Dispose(); second.Dispose();
            for (var repeat = 0; repeat < 2; repeat++) { using var cold = new LiteDatabase(files.Filename); Verify(cold, 1); }
        }

        [OneFileFact]
        public void Awakened_peer_legitimately_owns_WRITE_after_dead_pinned_holder_releases()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var first = new SharedEngine(files.Settings);
            using var peer = new SharedEngine(files.Settings);
            using var owned = new ManualResetEventSlim();
            using var exit = new ManualResetEventSlim();
            using var acquired = new ManualResetEventSlim();
            using var commit = new ManualResetEventSlim();
            IBsonDataReader anchor = null;
            SharedMutexPin pin = null;
            Exception ownerFailure = null, peerFailure = null;
            var owner = new Thread(() => ownerFailure = Record.Exception(() =>
            {
                first.PinIdleLimit = first.PinHoldLimit = TimeSpan.FromMinutes(10);
                anchor = first.Query("docs", new Query()); anchor.Read().Should().BeTrue();
                first.Update("docs", new[] { Row(1, 0) }); pin = first.Pin;
                pin.Should().NotBeNull();
                first.BeginTrans().Should().BeTrue(); first.Update("docs", new[] { Row(1, 99) });
                owned.Set(); exit.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            })) { IsBackground = true };
            var successor = new Thread(() => peerFailure = Record.Exception(() =>
            {
                peer.BeginTrans().Should().BeTrue(); acquired.Set();
                commit.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                peer.Update("docs", new[] { Row(1, 1) }); peer.Commit().Should().BeTrue();
            })) { IsBackground = true };
            var started = false;
            owner.Start();
            try
            {
                owned.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue(); successor.Start(); started = true;
                SpinWait.SpinUntil(() => (successor.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                Field<int>(peer, "_mutexWaiters").Should().Be(0);
                exit.Set(); owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                pin.WaitReleased();
                acquired.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                using var probe = new SharedRangeLocks(files.Filename + "-shm");
                Action whilePeerOwns = () => probe.EnterWrite();
                whilePeerOwns.Should().Throw<System.IO.IOException>().WithMessage("*WRITE ownership mismatch*");
            }
            finally
            {
                exit.Set(); commit.Set();
                owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (started) successor.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                anchor?.Dispose();
            }
            ownerFailure.Should().BeNull(); peerFailure.Should().BeNull();
            var successorPin = peer.Pin;
            if (successorPin != null) { successorPin.RequestRelease(false); successorPin.WaitReleased(); }
            files.WriteIsFree();
            first.Dispose(); peer.Dispose();
            for (var repeat = 0; repeat < 2; repeat++) { using var cold = new LiteDatabase(files.Filename); Verify(cold, 1); }
        }

        [OneFileTheory]
        [InlineData(false, false)] [InlineData(false, true)]
        [InlineData(true, false)] [InlineData(true, true)]
        public void Teardown_callback_disposal_refuses_reentry_and_still_releases_ownership(bool sibling, bool pinned)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var peer = sibling ? new SharedEngine(files.Settings) : null;
            engine.PinIdleLimit = engine.PinHoldLimit = TimeSpan.FromMinutes(10);
            using var anchor = pinned ? engine.Query("docs", new Query()) : null;
            if (anchor != null) { anchor.Read().Should().BeTrue(); engine.Update("docs", new[] { Row(1, 0) }); }
            engine.BeginTrans().Should().BeTrue(); engine.Update("docs", new[] { Row(1, 1) });
            var pin = engine.Pin;
            var hits = 0;
            Exception callbackFailure = null;
            engine.OwnershipStage = stage =>
            {
                if (stage != "core-closed") return;
                Interlocked.Increment(ref hits);
                callbackFailure = Record.Exception(engine.Dispose);
            };
            engine.Rollback().Should().BeTrue();
            if (pin != null) { pin.RequestRelease(force: false); pin.WaitReleased(); }
            engine.OwnershipStage = null;
            hits.Should().Be(1);
            callbackFailure.Should().BeOfType<InvalidOperationException>();
            files.WriteIsFree();
            if (peer != null) { peer.BeginTrans().Should().BeTrue(); peer.Rollback(); }
            engine.Dispose(); peer?.Dispose(); anchor?.Dispose();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 0);
        }

        [OneFileFact]
        public void Last_reference_close_racing_attach_waits_for_teardown_then_opens_a_fresh_table()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var first = new SharedEngine(files.Settings);
            var table = first.MappedReaderTable;
            using var closing = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var attempting = new ManualResetEventSlim();
            Exception closeFailure = null, attachFailure = null;
            SharedEngine next = null;
            var closer = new Thread(() =>
            {
                SharedMemoryAuthority.BootstrapStage = stage =>
                { if (stage == "before-close") { closing.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
                try { closeFailure = Record.Exception(first.Dispose); }
                finally { SharedMemoryAuthority.BootstrapStage = null; }
            }) { IsBackground = true };
            var attacher = new Thread(() =>
            { attempting.Set(); attachFailure = Record.Exception(() => next = new SharedEngine(files.Settings)); })
                { IsBackground = true };
            closer.Start();
            try
            {
                closing.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                attacher.Start(); attempting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                SpinWait.SpinUntil(() => (attacher.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                next.Should().BeNull();
            }
            finally
            {
                release.Set(); closer.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (attacher.ThreadState != ThreadState.Unstarted) attacher.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            }
            closeFailure.Should().BeNull(); attachFailure.Should().BeNull();
            using (next)
            {
                next.MappedReaderTable.Should().NotBeSameAs(table);
                using var db = new LiteDatabase(next, disposeOnClose: false); Verify(db, 0);
            }
        }
    }
}
#endif
