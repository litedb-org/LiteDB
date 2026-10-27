#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Tests;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedWriterTenureHandoffCheckpoint_Tests
    {
        private readonly ITestOutputHelper _output;
        public SharedWriterTenureHandoffCheckpoint_Tests(ITestOutputHelper output) => _output = output;

        [OneFileFact]
        public void Peer_attach_wake_does_not_checkpoint_the_outgoing_tenure()
        {
            _output.WriteLine(RuntimeInformation.FrameworkDescription);
            using var files = new SharedOneFilePublicationFixture();
            var checkpoints = 0;
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 1000;
                for (var value = 1; value <= 30; value++)
                    engine.Update("docs", new[] { Row(1, value) }).Should().Be(1);
                var core = Core(engine);
                var disk = Field<DiskService>(core, "_disk");
                var logicalPages = disk.GetFileLength(FileOrigin.Log) / Constants.PAGE_SIZE;
                _output.WriteLine("Logical WAL pages before wake: " + logicalPages);
                logicalPages.Should().BeGreaterThan(SharedEngine.CLOSE_CHECKPOINT_PAGES).And.BeLessThan(1000);
                State(core).CheckpointStage = stage =>
                {
                    if (stage == "before-commit-lock") Interlocked.Increment(ref checkpoints);
                };
                using (var peer = new MvccProcess("one-file-tenure", files.Filename, null))
                {
                    peer.Expect("ready").GetAwaiter().GetResult();
                    Core(engine).Should().BeNull("remote admission requires outgoing storage and WRITE to be released");
                    peer.Send("close");
                    peer.Expect("done").GetAwaiter().GetResult();
                    peer.Finish().GetAwaiter().GetResult();
                }
            }
            for (var pass = 0; pass < 2; pass++)
            {
                using var cold = new LiteDatabase(files.Filename);
                Verify(cold, 30);
            }
            checkpoints.Should().Be(0, "the owner requires release on wake without checkpointing");
        }
    }
}
#endif
