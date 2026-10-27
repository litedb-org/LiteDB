#if NET8_0_OR_GREATER
using System;
using System.Linq;
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
    public class SharedOneFileWarmReads_Tests
    {
        internal static void Warm(SharedEngine engine, LiteDatabase db)
        {
            engine.CoordinatedIdleLimit = TimeSpan.FromMinutes(1);
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 0));
            engine.HasCachedSnapshot.Should().BeTrue();
            engine.MutexOwner.WaitForRelease();
        }

        [OneFileFact]
        public void Process_cache_admission_has_zero_coordination_operations_even_while_main_mutex_is_owned()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var first = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(first, disposeOnClose: false);
            using var peer = new SharedEngine(files.Settings);
            using var peerDb = new LiteDatabase(peer, disposeOnClose: false);
            SharedCoordinationDiagnostics.AdmissionOperations = 0;
            Warm(first, db);
            SharedCoordinationDiagnostics.AdmissionOperations.Should().BeGreaterThan(0, "the counter must detect the protected path");
            var cache = first.MappedReaderTable.ReadCache.Snapshot;
            cache.Lease.Should().BeNull("idle cache must not pin a generation");
            first.MappedReaderTable.HasReaders.Should().BeFalse();
            first.MutexOwner.Enter(scoped: true);
            try
            {
                var read = Task.Run(() =>
                {
                    SharedCoordinationDiagnostics.AdmissionOperations = 0;
                    for (var i = 0; i < 12; i++) peerDb.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 0));
                    return SharedCoordinationDiagnostics.AdmissionOperations;
                });
                read.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("warm reads must finish under remote main ownership");
                read.GetAwaiter().GetResult().Should().Be(0);
                peer.CoordinatedReadHits.Should().Be(12);
                peer.SnapshotOpens.Should().Be(0, "the process cache is shared across facades");
            }
            finally { first.MutexOwner.Exit(); }
            first.Dispose();
            // Nonfinal disposal must leave the peer's actual cache available.
            peer.MappedReaderTable.ReadCache.Snapshot.Should().BeSameAs(cache);
            peerDb.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 0));
        }

        [OneFileTheory]
        [InlineData("read-sequence")]
        [InlineData("read-authority")]
        [InlineData("read-fields")]
        [InlineData("read-recheck")]
        public void Every_status_load_rejects_a_raced_64_bit_fence_without_executing_a_query(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            Warm(engine, db);
            var reads = engine.MappedReaderTable.Reads;
            var map = engine.MappedReaderTable.Authority.Map;
            var sequence = map.Load(SharedMemoryLayout.Control + 8);
            var reached = 0;
            SharedMemoryReadAdmission.AdmissionStage = stage =>
            {
                if (stage != cut) return;
                reached++;
                map.Store(SharedMemoryLayout.Control + 8, sequence + 2);
                // Crossing the 32-bit boundary must never produce a torn word.
                map.Store(SharedMemoryLayout.ReuseEpoch, 0x100000002L);
            };
            try { reads.TryRead(out _).Should().BeFalse(); }
            finally { SharedMemoryReadAdmission.AdmissionStage = null; }
            reached.Should().Be(1);
            reads.TryRead(out var after).Should().BeTrue();
            after.Reuse.Should().Be(0x100000002L);
            var hits = engine.CoordinatedReadHits;
            db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 0));
            engine.CoordinatedReadHits.Should().Be(hits, "changed storage requires a protected fresh snapshot");
        }

        [OneFileTheory]
        [InlineData("read-sequence", 1)]
        [InlineData("read-authority", 1)]
        [InlineData("read-fields", 1)]
        [InlineData("read-recheck", 1)]
        [InlineData("read-sequence", 2)]
        [InlineData("read-authority", 2)]
        [InlineData("read-fields", 2)]
        [InlineData("read-recheck", 2)]
        public void Sequence_change_at_each_load_before_and_after_record_publication_falls_back(string cut, int occurrence)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            Warm(engine, db);
            var table = engine.MappedReaderTable;
            var hits = engine.CoordinatedReadHits;
            var reached = 0;
            SharedMemoryReadAdmission.AdmissionStage = stage =>
            {
                if (stage != cut || ++reached != occurrence) return;
                SharedMemoryReadAdmission.AdmissionStage = null;
                table.HasReaders.Should().Be(occurrence == 2);
                var sequence = table.Authority.Map.Load(SharedMemoryLayout.Control + 8);
                table.Authority.Map.Store(SharedMemoryLayout.Control + 8, sequence + 2);
            };
            try { db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 0)); }
            finally { SharedMemoryReadAdmission.AdmissionStage = null; }
            reached.Should().Be(occurrence);
            engine.CoordinatedReadHits.Should().Be(hits, "even a same-version publication must reject the crossed interval");
            table.HasReaders.Should().BeFalse();
        }

        [OneFileTheory]
        [InlineData("cached-status")]
        [InlineData("lease-published")]
        public void Writer_scan_before_or_after_record_publication_preserves_full_old_snapshot_and_current_index(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var writer = new MvccProcess("one-file-warm-writer", files.Filename, null);
            writer.Expect("ready").GetAwaiter().GetResult();
            Warm(engine, db);
            var hits = engine.CoordinatedReadHits;
            var reached = 0;
            engine.CoordinationStage = stage =>
            {
                if (stage != cut) return;
                engine.CoordinationStage = null;
                reached++;
                writer.Send("commit");
                writer.Expect("committed").GetAwaiter().GetResult();
                writer.Send("checkpoint");
                writer.Expect("checkpointed").GetAwaiter().GetResult();
            };
            db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 1));
            reached.Should().Be(1);
            engine.CoordinatedReadHits.Should().Be(hits);
            Verify(db, 1);
            writer.Send("exit");
            writer.Finish().GetAwaiter().GetResult();
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Reclaimed_WAL_address_negative_control_requires_the_post_record_fence(bool skipRecheck)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            engine.CoordinatedIdleLimit = TimeSpan.FromMinutes(1);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var writer = new MvccProcess("one-file-warm-writer", files.Filename, null);
            writer.Expect("ready").GetAwaiter().GetResult();
            writer.Send("commit");
            writer.Expect("committed").GetAwaiter().GetResult();
            // Cache a distant page, leaving the target's WAL address unread.
            for (var i = 0; i < 3; i++) db.GetCollection("docs").FindById(160);
            engine.HasCachedSnapshot.Should().BeTrue();
            engine.UnsafeSkipCoordinationRecheck = skipRecheck;
            var reached = 0;
            engine.CoordinationStage = stage =>
            {
                if (stage != "cached-status") return;
                engine.CoordinationStage = null;
                reached++;
                // Truncation requires sole DMS. Supersede the cached frame instead:
                // partial checkpoint can retire it before this reader publishes.
                writer.Send("commit2");
                writer.Expect("committed").GetAwaiter().GetResult();
                writer.Send("checkpoint");
                writer.Expect("checkpointed").GetAwaiter().GetResult();
            };
            Action read = () => db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 2));
            if (skipRecheck) read.Should().Throw<Exception>("the negative control reads a reclaimed WAL position");
            else read.Should().NotThrow();
            reached.Should().Be(1);
            writer.Send("exit");
            writer.Finish().GetAwaiter().GetResult();
        }

        [OneFileTheory]
        [InlineData(SharedMemoryLayout.Control, 0L)]
        [InlineData(SharedMemoryLayout.Control + 8, 3L)]
        [InlineData(SharedMemoryLayout.RecoveryOwner, 9L)]
        [InlineData(SharedMemoryLayout.FastAdmissionDisabled, 1L)]
        [InlineData(SharedMemoryLayout.StructuralEpoch, 3L)]
        [InlineData(SharedMemoryLayout.CommittedVersion, 0x100000000L)]
        [InlineData(0, 17L)]
        [InlineData(SharedMemoryLayout.Page, 17L)]
        public void Ready_and_version_alone_never_authorize_uncertain_or_damaged_state(int offset, long value)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var table = engine.MappedReaderTable;
            table.Reads.TryRead(out _).Should().BeTrue();
            var original = table.Authority.Map.Load(offset);
            try
            {
                table.Authority.Map.Store(offset, value);
                table.Reads.TryRead(out _).Should().BeFalse();
            }
            finally { table.Authority.Map.Store(offset, original); }
            table.Reads.TryRead(out _).Should().BeTrue();
        }
    }
}
#endif
