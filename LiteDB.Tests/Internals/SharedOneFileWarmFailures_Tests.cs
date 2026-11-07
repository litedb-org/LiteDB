#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmFailures_Tests
    {
        [OneFileTheory]
        [InlineData("read-sequence")]
        [InlineData("read-authority")]
        [InlineData("read-fields")]
        [InlineData("read-recheck")]
        [InlineData("reader-record")]
        public void Admission_exception_releases_a_new_record_without_query_execution(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            var hits = engine.CoordinatedReadHits;
            var reached = 0;
            SharedMemoryReadAdmission.AdmissionStage = stage =>
            {
                if (stage != cut) return;
                reached++;
                throw new IOException("admission cut");
            };
            try
            {
                Action read = () => db.GetCollection("docs").FindById(1);
                read.Should().Throw<IOException>().WithMessage("admission cut");
            }
            finally { SharedMemoryReadAdmission.AdmissionStage = null; }
            reached.Should().Be(1);
            engine.CoordinatedReadHits.Should().Be(hits);
            engine.MappedReaderTable.HasReaders.Should().BeFalse();
            db.GetCollection("docs").FindById(1).As<object>().Should().BeEquivalentTo(Row(1, 0));
        }

        [OneFileTheory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(101)]
        public void Warm_query_preserves_failure_at_the_original_row_and_releases_every_lease(int failingRow)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            IBsonDataReader reader = null;
            var rows = 0;
            var hits = engine.CoordinatedReadHits;
            Action read = () =>
            {
                reader = engine.Query("docs", new Query
                { Select = "{value: SUBSTRING(payload, 0, IIF(_id = " + failingRow + ", -1, 1))}" });
                while (reader.Read())
                {
                    reader.Current["value"].AsString.Should().Be("x");
                    rows++;
                }
            };
            try { read.Should().Throw<ArgumentOutOfRangeException>(); }
            finally { reader?.Dispose(); }
            rows.Should().Be(failingRow - 1);
            engine.CoordinatedReadHits.Should().Be(hits + 1);
            engine.MappedReaderTable.HasReaders.Should().BeFalse();
            Verify(db, 0);
        }

        [OneFileFact]
        public void Idle_process_cache_does_not_root_a_facade_or_prevent_final_participant_release()
        {
            using var files = new SharedOneFilePublicationFixture();
            var abandoned = Abandon(files);
            for (var pass = 0; pass < 3; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            abandoned.Item1.IsAlive.Should().BeFalse();
            abandoned.Item2.Closed.Should().BeTrue("cached settings and timer must contain no facade ownership edge");
            using var cold = new LiteDatabase(files.Filename);
            Verify(cold, 0);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Tuple<WeakReference, SharedMemoryReaderTable> Abandon(SharedOneFilePublicationFixture files)
        {
            var engine = new SharedEngine(files.Settings) { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
            for (var i = 0; i < 3; i++)
            {
                using var reader = engine.Query("docs", new Query { Limit = 1 });
                reader.Read().Should().BeTrue();
            }
            engine.HasCachedSnapshot.Should().BeTrue();
            engine.MutexOwner.WaitForRelease();
            // The inherited first-read holder exits after its own one-second idle
            // policy. Isolate cache/timer roots from that intentional thread root.
            var holder = Field<System.Threading.Thread>(engine.MutexOwner, "_holder");
            if (holder != null) holder.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            return Tuple.Create(new WeakReference(engine), engine.MappedReaderTable);
        }

        [OneFileFact]
        public void Concurrent_aligned_epoch_loads_never_observe_a_torn_64_bit_value()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            var map = engine.MappedReaderTable.Authority.Map;
            const long first = 0x100000002L, second = 0x300000004L;
            map.Store(SharedMemoryLayout.ReuseEpoch, first);
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 20000; i++) map.Store(SharedMemoryLayout.ReuseEpoch, (i & 1) == 0 ? first : second);
            });
            for (var i = 0; i < 20000; i++)
            {
                var value = map.Load(SharedMemoryLayout.ReuseEpoch);
                (value == first || value == second).Should().BeTrue();
            }
            writer.GetAwaiter().GetResult();
        }
    }
}
#endif
