#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenure_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Failed_completion_drains_the_inner_transaction_before_idle_reuse(bool commit)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            {
                engine.BeginTrans(); engine.Update("docs", new[] { Row(1, 99) });
                var core = Core(engine);
                var hits = 0;
                engine.OwnershipStage = stage =>
                {
                    if (stage != (commit ? "before-commit" : "before-rollback")) return;
                    hits++;
                    throw new IOException("completion cut");
                };
                try
                {
                    ((Action)(() => { if (commit) engine.Commit(); else engine.Rollback(); }))
                        .Should().Throw<IOException>().WithMessage("completion cut");
                }
                finally { engine.OwnershipStage = null; }
                hits.Should().Be(1);
                core.HasActiveTransaction.Should().BeFalse();
                Core(engine).Should().BeNull();
                files.WriteIsFree();
                engine.BeginTrans().Should().BeTrue();
                engine.Update("docs", new[] { Row(1, 1) }); engine.Commit().Should().BeTrue();
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
        }

        [OneFileFact]
        public void Transaction_budget_and_time_budget_are_independent_and_reset_when_alone()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var authority = engine.MappedReaderTable.Authority;
            var count = new SharedWriterTenure(authority, millisecondBudget: int.MaxValue);
            var time = new SharedWriterTenure(authority, transactionBudget: int.MaxValue);
            using (authority.Requests.Enter())
            {
                for (var n = 0; n < SharedWriterTenure.TransactionBudget - 1; n++) count.ShouldEnd(boundary: true).Should().BeFalse();
                count.ShouldEnd(boundary: true).Should().BeTrue();
                time.ShouldEnd();
                Thread.Sleep(5);
                time.ShouldEnd().Should().BeTrue();
            }
            count.ShouldEnd().Should().BeFalse();
            time.ShouldEnd().Should().BeFalse();
        }

        [OneFileFact]
        public void Retained_writer_can_warm_independent_readers_without_reopening_or_exposing_uncommitted_rows()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            using (var peer = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                engine.Update("docs", new[] { Row(1, 0) });
                var core = Core(engine);
                SharedOneFileWarmReads_Tests.Warm(engine, db);
                Core(engine).Should().BeSameAs(core);
                engine.EngineOpens.Should().Be(1);
                using var old = peer.Query("docs", new Query());
                old.Read().Should().BeTrue();
                engine.BeginTrans(); engine.Update("docs", new[] { Row(1, 1) });
                var hits = peer.CoordinatedReadHits;
                using (var independent = peer.Query("docs", new Query()))
                {
                    var n = 0;
                    while (independent.Read()) independent.Current.Should().BeEquivalentTo(Row(++n, 0));
                    n.Should().Be(160);
                }
                peer.CoordinatedReadHits.Should().BeGreaterThan(hits);
                engine.Commit().Should().BeTrue();
                var remaining = 1;
                while (old.Read()) old.Current.Should().BeEquivalentTo(Row(++remaining, 0));
                remaining.Should().Be(160);
            }
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Alone_keeps_main_WRITE_and_the_same_core_across_transactions_and_idle(bool explicitTransactions)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            {
                var table = engine.MappedReaderTable;
                var initialLocks = table.Authority.Locks.WriteAcquisitions;
                for (var value = 1; value <= 24; value++)
                {
                    if (explicitTransactions) engine.BeginTrans().Should().BeTrue();
                    engine.Update("docs", new[] { Row(1, value) }).Should().Be(1);
                    if (explicitTransactions) engine.Commit().Should().BeTrue();
                    if (value == 1)
                    {
                        Field<SharedMutexTurnstile>(engine, "_turnstile").MainAcquisitions.Should().Be(1);
                        engine.EngineOpens.Should().Be(1);
                        table.Authority.Locks.WriteAcquisitions.Should().Be(initialLocks + 1);
                    }
                }
                var core = Core(engine);
                var pin = engine.Pin;
                // Longer than the old idle/total pin deadlines; no throughput measurement.
                Thread.Sleep(1100);
                Field<SharedMutexTurnstile>(engine, "_turnstile").MainAcquisitions.Should().Be(1);
                Core(engine).Should().BeSameAs(core);
                engine.Pin.Should().BeSameAs(pin);
                engine.EngineOpens.Should().Be(1);
                table.Authority.Locks.WriteAcquisitions.Should().Be(initialLocks + 1);
                using var probe = new SharedRangeLocks(files.Filename + "-shm");
                Action acquire = probe.EnterWrite;
                acquire.Should().Throw<IOException>("the idle core still owns actual WRITE");
                using var main = SharedMutexFactory.Create(SharedMutexNameFactory.Create(files.Filename, files.Settings.SharedMutexNameStrategy));
                main.WaitOne(0).Should().BeFalse("the dedicated holder still owns main");
            }
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename);
            Verify(cold, 24);
        }

        [OneFileFact]
        public void Idle_tenure_can_move_to_another_application_thread_without_reopening()
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            {
                engine.Update("docs", new[] { Row(1, 1) });
                var core = Core(engine);
                var locks = engine.MappedReaderTable.Authority.Locks.WriteAcquisitions;
                Exception failure = null;
                var next = new Thread(() => failure = Record.Exception(() =>
                {
                    engine.BeginTrans().Should().BeTrue();
                    engine.Update("docs", new[] { Row(1, 2) });
                    engine.Commit().Should().BeTrue();
                }));
                next.Start(); next.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                failure.Should().BeNull();
                Core(engine).Should().BeSameAs(core);
                engine.MappedReaderTable.Authority.Locks.WriteAcquisitions.Should().Be(locks);
            }
            using var cold = new LiteDatabase(files.Filename);
            Verify(cold, 2);
        }

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Request_without_main_wakes_holder_and_hands_off_at_transaction_boundary(bool stopped)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var holder = new SharedEngine(files.Settings))
            {
                holder.BeginTrans(); holder.Update("docs", new[] { Row(1, 1) });
                var pin = holder.Pin;
                var core = Core(holder);
                var boundaries = 0;
                holder.OwnershipStage = stage =>
                {
                    if (stage != "tenure-handoff") return;
                    Interlocked.Increment(ref boundaries);
                    core.HasActiveTransaction.Should().BeFalse();
                    if (stopped) State(core).Stop(new IOException("stop at handoff"));
                };
                using var requester = new MvccProcess("one-file-tenure-request", files.Filename, null);
                requester.Expect("requested").GetAwaiter().GetResult();
                requester.Send("go"); requester.Expect("waiting-main").GetAwaiter().GetResult();
                // The requester reached its record without owning main. It cannot
                // finish protected construction while the explicit transaction lives.
                holder.MappedReaderTable.Authority.Map.Load(SharedMemoryLayout.WriterOwner).Should().NotBe(0);
                core.HasActiveTransaction.Should().BeTrue();
                holder.Commit().Should().BeTrue();
                requester.Expect("ready").GetAwaiter().GetResult();
                pin.WaitReleased();
                boundaries.Should().Be(1);
                requester.Send("write 901"); requester.Expect("wrote 901").GetAwaiter().GetResult();
                requester.Send("close"); requester.Expect("done").GetAwaiter().GetResult(); requester.Finish().GetAwaiter().GetResult();
                holder.OwnershipStage = null;
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(files.Filename);
                Verify(cold, 1);
                cold.GetCollection("tenure").FindAll().Select(d => d["_id"].AsInt32).Should().Equal(901);
                cold.GetCollection("tenure").Find(Query.EQ("value", 901)).Single()["payload"].AsString.Should().Be(new string('t', 256));
            }
        }

        [OneFileTheory]
        [InlineData("request-published")] [InlineData("request-admitted")]
        public void Request_fault_releases_its_record_and_does_not_poison_the_next_writer(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using (var engine = new SharedEngine(files.Settings))
            {
                var hits = 0;
                SharedWriterRequests.RequestStage = stage =>
                { if (stage == cut) { hits++; throw new IOException(cut); } };
                try { ((Action)(() => engine.BeginTrans())).Should().Throw<IOException>().WithMessage(cut); }
                finally { SharedWriterRequests.RequestStage = null; }
                hits.Should().Be(1);
                var table = engine.MappedReaderTable;
                table.Authority.Map.Load(table.Authority.Block + SharedMemoryLayout.TenureRequest).Should().Be(0);
                engine.BeginTrans().Should().BeTrue();
                engine.Update("docs", new[] { Row(1, 1) }); engine.Commit().Should().BeTrue();
            }
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename); Verify(cold, 1);
        }
    }
}
#endif
