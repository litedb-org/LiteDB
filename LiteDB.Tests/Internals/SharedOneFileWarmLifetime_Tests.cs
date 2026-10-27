#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmLifetime_Tests
    {
        [OneFileTheory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Collected_result_releases_pages_generation_and_final_participant_after_owner_exit(bool warm, bool close)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            if (warm) SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            try
            {
                WeakReference weak = null;
                Exception error = null;
                var owner = new Thread(() =>
                {
                    try { weak = Abandon(engine); }
                    catch (Exception ex) { error = ex; }
                });
                owner.Start();
                owner.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
                error.Should().BeNull();
                table.HasReaders.Should().BeTrue();
                if (close) engine.Dispose();
                Collect();
                weak.IsAlive.Should().BeFalse();
                SpinWait.SpinUntil(() => !table.HasReaders, TimeSpan.FromSeconds(5)).Should().BeTrue();
                if (close) SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
                else
                {
                    table.ReadCache.IdleLimit = TimeSpan.Zero;
                    table.ReadCache.Expire();
                    Verify(db, 0);
                }
                Collect();
                finalized.Should().Be(0, "the cleanup root must protect pages until their pins are released");
            }
            finally { PageBuffer.FinalizedInUse = null; }
        }

        [OneFileFact]
        public void A_blocked_cleanup_cannot_block_the_finalizer_or_finalize_its_pinned_pages()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var snapshot = table.ReadCache.Snapshot;
            var weak = Abandon(engine);
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            try
            {
                lock (table.ReadCache.Gate)
                {
                    // Cleanup needs this gate; GC must finish while it stays held.
                    var collector = Task.Run(Collect);
                    collector.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                    weak.IsAlive.Should().BeFalse();
                    snapshot.Readers.Should().Be(1);
                    finalized.Should().Be(0);
                }
                SpinWait.SpinUntil(() => !table.HasReaders, TimeSpan.FromSeconds(5)).Should().BeTrue();
                Verify(db, 0);
                Collect();
                finalized.Should().Be(0);
            }
            finally { PageBuffer.FinalizedInUse = null; }
        }

        [OneFileFact]
        public async Task Collected_warm_result_allows_remote_drain_whether_empty_WAL_is_removed_or_retained()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var writer = new MvccProcess("one-file-warm-writer", files.Filename, null);
            await writer.Expect("ready");
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var weak = Abandon(engine);
            Collect();
            weak.IsAlive.Should().BeFalse();
            SpinWait.SpinUntil(() => !table.HasReaders, TimeSpan.FromSeconds(5)).Should().BeTrue();
            writer.Send("commit");
            await writer.Expect("committed");
            writer.Send("checkpoint");
            await writer.Expect("checkpointed");
            (File.Exists(files.Log) ? new FileInfo(files.Log).Length : 0).Should().Be(0);
            writer.Send("exit");
            await writer.Finish();
            Verify(db, 1);
        }

        [OneFileTheory]
        [InlineData("cached-status")]
        [InlineData("lease-published")]
        public void Final_facade_disposal_between_admission_and_counting_cannot_unmap_a_published_record(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            using var reached = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            engine.CoordinationStage = stage =>
            {
                if (stage != cut) return;
                reached.Set();
                resume.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            };
            var read = Task.Run(() =>
            {
                Action query = () => { using var result = engine.Query("docs", new Query()); };
                query.Should().Throw<ObjectDisposedException>();
            });
            Task dispose = null;
            try
            {
                reached.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                dispose = Task.Run(engine.Dispose);
                SpinWait.SpinUntil(() => Field<int>(engine, "_disposed") != 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                dispose.IsCompleted.Should().BeFalse("unmapping must wait for the admitting call's cache gate");
                table.Authority.Map.Load(SharedMemoryLayout.Control).Should().Be(1);
            }
            finally
            {
                resume.Set();
                read.GetAwaiter().GetResult();
                dispose?.GetAwaiter().GetResult();
            }
            table.Closed.Should().BeTrue();
            table.HasReaders.Should().BeFalse();
        }

        [OneFileFact]
        public void Default_Shared_cold_abandonment_also_releases_the_preexisting_dev_page_leak()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(new EngineSettings { Filename = files.Filename });
            var finalized = 0;
            PageBuffer.FinalizedInUse = _ => Interlocked.Increment(ref finalized);
            try
            {
                var weak = Abandon(engine);
                Collect();
                weak.IsAlive.Should().BeFalse();
                var owners = Field<System.Collections.Generic.Dictionary<Thread, int>>(engine, "_localReaders");
                SpinWait.SpinUntil(() => { lock (owners) return owners.Count == 0; }, TimeSpan.FromSeconds(5)).Should().BeTrue();
                engine.Dispose();
                Collect();
                finalized.Should().Be(0);
                using var reopened = new LiteDatabase(files.Filename);
                Verify(reopened, 0);
            }
            finally { PageBuffer.FinalizedInUse = null; }
        }

        [OneFileFact]
        public void Collected_retired_warm_result_releases_only_its_generation_while_current_stream_remains_live()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var writer = new SharedEngine(files.Settings);
            using var writerDb = new LiteDatabase(writer, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var table = engine.MappedReaderTable;
            var old = table.ReadCache.Snapshot;
            var weak = Abandon(engine);
            Task.Run(() => writerDb.GetCollection("docs").Update(Row(1, 1))).GetAwaiter().GetResult();
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1)["value"].AsInt32.Should().Be(1);
            using (var current = engine.Query("docs", new Query()))
            {
                var accepted = table.ReadCache.Snapshot;
                old.Retired.Should().BeTrue();
                table.LiveVersions().Should().HaveCount(2);
                Collect();
                weak.IsAlive.Should().BeFalse();
                SpinWait.SpinUntil(() => old.Readers == 0, TimeSpan.FromSeconds(5)).Should().BeTrue();
                table.LiveVersions().Should().Equal((int)accepted.Status.Version);
                accepted.Readers.Should().Be(1);
                var count = 0;
                while (current.Read())
                {
                    var row = current.Current;
                    row["value"].AsInt32.Should().Be(row["_id"].AsInt32 == 1 ? 1 : 0);
                    row["payload"].AsString.Should().Be(new string('x', 1024));
                    count++;
                }
                count.Should().Be(160);
            }
            table.HasReaders.Should().BeFalse();
            Verify(db, 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(SharedEngine engine)
        {
            var reader = engine.Query("docs", new Query());
            reader.Read().Should().BeTrue();
            return new WeakReference(reader);
        }

        private static void Collect()
        {
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }
    }
}
#endif
