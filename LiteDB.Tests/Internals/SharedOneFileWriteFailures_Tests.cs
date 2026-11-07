#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWriteFailures_Tests
    {
        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_append_cleanup_publishes_reuse_and_failed_truncation_remains_fenced(bool failCleanup)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1, 1));
            var core = Core(engine);
            var state = State(core);
            var table = engine.MappedReaderTable;
            var reuse = table.Authority.Map.Load(SharedMemoryLayout.ReuseEpoch);
            var writes = 0;
            var truncations = 0;
            var completedTruncations = 0;
            state.SimulateDiskWriteFail = _ =>
            {
                if (++writes == 1) throw new IOException("injected append failure");
            };
            state.AtCrashPoint = stage =>
            {
                if (stage == "wal-after-failed-append-truncate") completedTruncations++;
                if (stage != "wal-before-failed-append-truncate") return;
                truncations++;
                table.Authority.Map.Load(SharedMemoryLayout.ReuseEpoch).Should().BeGreaterThan(reuse);
                if (failCleanup) throw new IOException("injected failed truncation");
            };
            try
            {
                Action commit = () => db.Commit();
                commit.Should().Throw<IOException>();
                writes.Should().BeGreaterThan(0);
                truncations.Should().Be(1);
                completedTruncations.Should().Be(failCleanup ? 0 : 1);
                if (failCleanup) { state.Stopped.Should().BeTrue(); Fenced(table); }
                files.WriteIsFree();
            }
            finally { state.SimulateDiskWriteFail = null; state.AtCrashPoint = null; }
            for (var repeat = 0; repeat < 2; repeat++) { Verify(db, 0); db.Checkpoint(); Quiet(table); }
        }

        [OneFileFact]
        public void Safepoint_overwrite_failure_is_terminal_and_keeps_all_committed_rows()
        {
            using var files = new SharedOneFilePublicationFixture();
            var settings = files.Settings;
            settings.TransactionPageLimit = 1;
            using var engine = new SharedEngine(settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans();
            var core = Core(engine);
            var state = State(core);
            var table = engine.MappedReaderTable;
            var reuse = table.Authority.Map.Load(SharedMemoryLayout.ReuseEpoch);
            var reached = false;
            state.AtCrashPoint = stage =>
            {
                if (stage != "wal-page-after-write" || table.Authority.Map.Load(SharedMemoryLayout.ReuseEpoch) == reuse) return;
                reached = true;
                throw new IOException("uncertain safepoint overwrite");
            };
            try
            {
                Action update = () =>
                {
                    for (var revision = 1; revision < 5; revision++)
                        db.GetCollection("docs").Update(Enumerable.Range(1, 160).Select(id => Row(id, revision)));
                    db.Commit();
                };
                update.Should().Throw<IOException>();
                reached.Should().BeTrue("force an actual prefix overwrite after a safepoint");
                state.Stopped.Should().BeTrue();
                // Reconcile the wrapper's explicit hold even though the inner transaction stopped.
                try { db.Rollback(); } catch (IOException) { }
                Fenced(table);
                files.WriteIsFree();
            }
            finally { state.AtCrashPoint = null; }
            for (var repeat = 0; repeat < 2; repeat++) { Verify(db, 0); db.Checkpoint(); Quiet(table); }
        }

        [OneFileTheory]
        [InlineData("writer-epoch")]
        [InlineData("writer-owner")]
        [InlineData("recovery-owner")]
        [InlineData("fence-begin")]
        [InlineData("open-begin")]
        [InlineData("open-end")]
        public void Interrupted_writer_acquisition_unwinds_native_and_thread_ownership(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var hit = false;
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != cut || hit) return;
                hit = true;
                throw new IOException("writer admission interrupted");
            };
            try
            {
                Action update = () => db.GetCollection("docs").Update(Row(1, 1));
                update.Should().Throw<IOException>();
                hit.Should().BeTrue();
                files.WriteIsFree();
                Fenced(engine.MappedReaderTable);
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; }
            // A different actual thread must make progress after the failed claim.
            System.Threading.Tasks.Task.Run(() => { _ = db.Timeout; Verify(db, 0); }).Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            Quiet(engine.MappedReaderTable);
        }

        [OneFileTheory]
        [InlineData(0)]
        [InlineData(1)]
        public void Saturated_admission_counters_refuse_without_leaking_WRITE_or_mutating_storage(int counter)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var map = engine.MappedReaderTable.Authority.Map;
            var offset = counter == 0 ? SharedMemoryLayout.WriterTenureEpoch : SharedMemoryLayout.Control + 8;
            var previous = map.Load(offset);
            var data = File.ReadAllBytes(files.Filename);
            var wal = File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>();
            map.Store(offset, long.MaxValue);
            try
            {
                Action write = () => db.GetCollection("docs").Update(Row(1, 1));
                write.Should().Throw<OverflowException>();
                files.WriteIsFree();
                File.ReadAllBytes(files.Filename).Should().Equal(data);
                (File.Exists(files.Log) ? File.ReadAllBytes(files.Log) : Array.Empty<byte>()).Should().Equal(wal);
            }
            finally { map.Store(offset, previous); }
            Verify(db, 0);
        }
    }
}
#endif
