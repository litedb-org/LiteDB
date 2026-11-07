using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class OwnershipLease_Tests
    {
        [Fact]
        public async Task Collection_recursion_follows_identity_across_threads_and_wrong_release_preserves_owner()
        {
            var gate = new CollectionLock();
            var owner = TransactionLease.Handle(new object());
            var peer = TransactionLease.Handle(owner.Facade);
            gate.TryEnter(owner, TimeSpan.Zero).Should().BeTrue();
            await Task.Run(() => gate.TryEnter(owner, TimeSpan.Zero).Should().BeTrue());
            Action wrong = () => gate.Exit(peer);
            wrong.Should().Throw<SynchronizationLockException>();
            gate.TryEnter(peer, TimeSpan.Zero).Should().BeFalse();
            await Task.Run(() => gate.Exit(owner));
            gate.TryEnter(peer, TimeSpan.Zero).Should().BeFalse();
            gate.Exit(owner);
            gate.TryEnter(peer, TimeSpan.Zero).Should().BeTrue();
            gate.Exit(peer);
            owner.Token.Should().NotBeSameAs(peer.Token);
            owner.Generation.Should().NotBe(peer.Generation);
            owner.Thread.Should().BeNull();
        }

        [Fact]
        public async Task Collection_waiter_advances_only_after_final_owner_release()
        {
            var gate = new CollectionLock();
            var first = new object();
            var next = new object();
            using var waiting = new ManualResetEventSlim();
            gate.BeforeWait = () => waiting.Set();
            gate.TryEnter(first, TimeSpan.Zero).Should().BeTrue();
            var waiter = Task.Run(() =>
            {
                gate.TryEnter(next, TimeSpan.FromSeconds(5)).Should().BeTrue();
                gate.Exit(next);
            });
            try
            {
                waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                waiter.IsCompleted.Should().BeFalse();
            }
            finally { gate.Exit(first); }
            await waiter;
        }

        [Fact]
        public async Task Admission_release_on_another_thread_does_not_release_peer_lease()
        {
            using var gate = new TransactionGate();
            var first = new object();
            var next = new object();
            gate.TryEnterReadLock(TimeSpan.Zero, first).Should().BeTrue();
            gate.TryEnterReadLock(TimeSpan.Zero, next).Should().BeTrue();
            await Task.Run(() => gate.ExitReadLock(first));
            gate.CurrentReadCount.Should().Be(1);
            gate.TryEnterWriteLock(TimeSpan.Zero).Should().BeFalse();
            gate.ExitReadLock(first); // stale release must leave next intact
            gate.CurrentReadCount.Should().Be(1);
            gate.ExitReadLock(next);
            gate.TryEnterWriteLock(TimeSpan.Zero).Should().BeTrue();
            gate.ExitWriteLock();
        }

        [Fact]
        public void Shared_handle_survives_creator_exit_and_stale_cleanup_cannot_release_successor()
        {
            var gate = new SharedLocalTransactionOwner();
            var facade = new object();
            SharedLocalTransactionOwner.Lease first = null;
            var creator = new Thread(() => first = gate.Enter(TransactionLease.Handle(facade), TimeSpan.Zero));
            creator.Start();
            creator.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            Action waiting = () => gate.Enter(TransactionLease.Handle(facade), TimeSpan.Zero);
            waiting.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(120);
            first.Dispose();
            using var successor = gate.Enter(TransactionLease.Handle(facade), TimeSpan.Zero);
            first.Dispose();
            waiting.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(120);
        }

        [Fact]
        public void Shared_legacy_owner_rejects_same_thread_handle_peer_with_141()
        {
            var gate = new SharedLocalTransactionOwner();
            using var legacy = gate.Enter(new object());
            Action enter = () => gate.Enter(TransactionLease.Handle(new object()), TimeSpan.Zero);
            enter.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(141);
        }

        [Fact]
        public async Task Monitor_handle_release_preserves_unrelated_legacy_slot_and_file_state()
        {
            using var file = new TempFile();
            using (var engine = new LiteEngine(file.Filename))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
                var monitor = engine.GetMonitor();
                var handle = monitor.CreateTransaction(TransactionLease.Handle(monitor.Owner));
                var second = monitor.CreateTransaction(TransactionLease.Handle(monitor.Owner));
                handle.Lease.LockOwner.Should().NotBeSameAs(second.Lease.LockOwner);
                db.BeginTrans().Should().BeTrue();
                var legacy = monitor.GetThreadTransaction();
                await Task.Run(() =>
                {
                    handle.Rollback();
                    monitor.ReleaseTransaction(handle);
                    second.Rollback();
                    monitor.ReleaseTransaction(second);
                });
                // The continuation can run on a foreign thread: assert the original
                // legacy object remains registered rather than consulting its slot.
                monitor.Transactions.Should().ContainSingle().Which.Should().BeSameAs(legacy);
                legacy.State.Should().Be(TransactionState.Active);
                // Read-only legacy cleanup can be performed by owner disposal.
            }
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("sentinel").FindById(1)["value"].AsString.Should().Be("preserved");
            reopened.GetCollection("sentinel").Count().Should().Be(1);
        }

        [Fact]
        public void Monitor_peer_handle_begin_obeys_legacy_141_without_harming_owner()
        {
            using var engine = new LiteEngine(":memory:");
            var monitor = engine.GetMonitor();
            var peer = monitor.CreateOwner();
            var legacy = monitor.GetTransaction(true, false, out _);
            Action begin = () => peer.CreateTransaction(TransactionLease.Handle(peer.Owner));
            begin.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(141);
            legacy.State.Should().Be(TransactionState.Active);
            legacy.Rollback();
            monitor.ReleaseTransaction(legacy);
            var handle = peer.CreateTransaction(TransactionLease.Handle(peer.Owner));
            handle.Rollback();
            peer.ReleaseTransaction(handle);
            monitor.Transactions.Should().BeEmpty();
            peer.CloseOwner();
        }

        [Fact]
        public void Ticket_wait_assertions_detect_monitors_and_retained_snapshots()
        {
            var header = new HeaderPage(new PageBuffer(new byte[Constants.PAGE_SIZE], 0, 0), 0);
            var writer = new object();
            Action wait = () => OwnershipAssertions.BeforeTicketWait(header, writer, 0);
            wait.Should().NotThrow();
            lock (header) wait.Should().Throw<InvalidOperationException>();
            lock (header.PublicationLock) wait.Should().Throw<InvalidOperationException>();
            lock (writer) wait.Should().Throw<InvalidOperationException>();
            Action retained = () => OwnershipAssertions.BeforeTicketWait(header, writer, 1);
            retained.Should().Throw<InvalidOperationException>();
        }
    }
}
