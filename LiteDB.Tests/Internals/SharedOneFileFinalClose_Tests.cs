#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public class SharedOneFileFinalClose_Tests
    {
        public static IEnumerable<object[]> Boundaries => new[] { "exclusive", "main", "write", "opened", "checkpointed",
            "disabled", "retired", "main-released", "slot-cleared", "dms-released" }
            .SelectMany(cut => new[] { new object[] { null, cut }, new object[] { "secret", cut } });
        [OneFileTheory]
        [MemberData(nameof(Boundaries))]
        public async Task Every_final_close_death_recovers_twice_without_changing_authority(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            Guid authority;
            using (var first = new SharedEngine(files.Settings)) authority = first.MappedReaderTable.Authority.Header.Authority;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var child = new MvccProcess("one-file-final-cut", files.Filename, password, "final:" + cut);
                await child.Expect("ready");
                await child.Kill();
                SharedOneFileMaintenanceCrash_Tests.Recover(files);
                using var reopened = new SharedEngine(files.Settings);
                reopened.MappedReaderTable.Authority.Header.Authority.Should().Be(authority);
            }
        }
        internal static readonly string[] CheckpointCuts = { "checkpoint-before-page-write", "checkpoint-after-page-write",
            "checkpoint-before-data-flush", "checkpoint-after-data-flush", "checkpoint-before-clear", "checkpoint-after-clear" };
        public static IEnumerable<object[]> CheckpointBoundaries => CheckpointCuts.SelectMany(cut => new[] {
            new object[] { null, cut }, new object[] { "secret", cut } });
        [OneFileTheory]
        [MemberData(nameof(CheckpointBoundaries))]
        public async Task Death_inside_final_checkpoint_recovers_the_complete_committed_state(string password, string cut)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var child = new MvccProcess("one-file-final-cut", files.Filename, password, "checkpoint:" + cut);
                await child.Expect("ready");
                await child.Kill();
                SharedOneFileMaintenanceCrash_Tests.Recover(files);
            }
        }
        [OneFileTheory]
        [InlineData("checkpoint-before-page-write")]
        [InlineData("checkpoint-after-page-write")]
        [InlineData("checkpoint-before-data-flush")]
        [InlineData("checkpoint-after-data-flush")]
        public void Failed_final_checkpoint_does_not_publish_or_retire_stopped_storage(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            var reached = false;
            var published = false;
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 1000;
                db.GetCollection("docs").Update(Row(1, 11));
                EngineState.SimulateProcessCrash = stage =>
                { if (stage == cut) { reached = true; throw new IOException("final checkpoint failed"); } };
                SharedMemoryReaderTable.PublicationStage = stage =>
                {
                    if (!reached) return;
                    events.Enqueue(stage);
                    if (stage == "writer-end" || stage == "recovery-cleared" || stage == "version-reset") published = true;
                };
                SharedMemoryFinalClose.FinalCloseStage = stage =>
                { if (reached) { events.Enqueue("final:" + stage); if (stage == "retired") published = true; } };
                try { engine.Dispose(); }
                finally
                {
                    EngineState.SimulateProcessCrash = null;
                    SharedMemoryReaderTable.PublicationStage = null;
                    SharedMemoryFinalClose.FinalCloseStage = null;
                }
                reached.Should().BeTrue();
                published.Should().BeFalse("stopped disposal must leave recovery to an independently validated opener: {0}", string.Join(",", events));
            }
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }
        [OneFileFact]
        public void Last_participant_checkpoints_below_threshold_and_leaves_reusable_retired_mapping()
        {
            using var files = new SharedOneFilePublicationFixture();
            Guid authority;
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                authority = engine.MappedReaderTable.Authority.Header.Authority;
                db.CheckpointSize = 1000;
                db.GetCollection("docs").Update(Row(1, 11));
                File.Exists(files.Log).Should().BeTrue();
            }
            if (File.Exists(files.Log)) new FileInfo(files.Log).Length.Should().Be(0);
            using (var file = File.OpenRead(files.Filename + "-shm"))
            {
                file.Position = SharedMemoryLayout.Control;
                using var reader = new BinaryReader(file);
                reader.ReadInt64().Should().Be(0);
            }
            using var reopened = new SharedEngine(files.Settings);
            reopened.MappedReaderTable.Authority.Header.Authority.Should().Be(authority);
            using var database = new LiteDatabase(reopened, disposeOnClose: false);
            Verify(database, 11);
        }
        [OneFileFact]
        public async Task Closing_writer_preserves_remote_warm_reader_then_last_process_drains()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 1000;
            using (var peer = new MvccProcess("one-file-maintenance-peer", files.Filename, null))
            {
                await peer.Expect("ready");
                db.GetCollection("docs").Update(Row(1, 11));
                engine.Dispose();
                new FileInfo(files.Log).Length.Should().BeGreaterThan(0);
                await peer.Finish(release: true);
            }
            if (File.Exists(files.Log)) new FileInfo(files.Log).Length.Should().Be(0);
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
        }
    }
}
#endif
