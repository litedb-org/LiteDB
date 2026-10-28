#if NET8_0_OR_GREATER
using System;
using System.IO;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileCheckpointFences_Tests
    {
        [OneFileTheory]
        [InlineData("checkpoint-before-page-write")]
        [InlineData("checkpoint-after-page-write")]
        [InlineData("checkpoint-before-data-flush")]
        [InlineData("checkpoint-after-data-flush")]
        public void Failed_checkpoint_unwinding_cannot_publish_or_clear_the_terminal_recovery_fence(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.CheckpointSize = 0;
            db.GetCollection("docs").Update(Row(1, 1)).Should().BeTrue();
            var table = engine.MappedReaderTable;
            var reached = false;
            var cleared = false;
            EngineState.SimulateProcessCrash = stage =>
            {
                if (stage != cut) return;
                reached = true;
                throw new IOException("mapped checkpoint stopped");
            };
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (reached && (stage == "recovery-cleared" || stage == "writer-end" || stage == "version-reset")) cleared = true;
            };
            try
            {
                Action checkpoint = () => db.Checkpoint();
                checkpoint.Should().Throw<IOException>();
                reached.Should().BeTrue();
                cleared.Should().BeFalse("a terminal checkpoint must not publish success while its scopes unwind");
                Fenced(table);
                files.WriteIsFree();
            }
            finally { EngineState.SimulateProcessCrash = null; SharedMemoryReaderTable.PublicationStage = null; }
            for (var recovery = 0; recovery < 2; recovery++) { Verify(db, 1); db.Checkpoint(); Quiet(table); }
        }
    }
}
#endif
