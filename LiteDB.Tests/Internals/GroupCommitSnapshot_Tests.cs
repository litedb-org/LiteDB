#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class GroupCommitSnapshot_Tests
    {
        [Fact]
        public void Dependency_capture_does_not_open_the_writer_before_registering_a_snapshot_pin()
        {
            using var file = new TempFile();
            using var engine = new LiteEngine(new EngineSettings { Filename = file.Filename });
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var disk = (DiskService)typeof(LiteEngine).GetField("_disk", flags).GetValue(engine);
            var index = (WalIndexService)typeof(LiteEngine).GetField("_walIndex", flags).GetValue(engine);
            typeof(DiskService).GetField("_commitGroup", flags).GetValue(disk).Should().BeNull();
            var writerField = typeof(DiskService).GetField("_writer", flags);
            var writer = writerField.GetValue(disk);
            var attempts = 0;
            writerField.SetValue(disk, new Lazy<Stream>(() =>
            {
                attempts++;
                throw new IOException("WAL writer open failed");
            }));
            try
            {
                var pages = new TransactionPages();
                var version = index.PinSnapshot(pages);
                try
                {
                    index.SnapshotCount.Should().Be(1);
                    pages.CommitTicket.Should().Be(0, "there has been no grouped append in this engine");
                    attempts.Should().Be(0, "capturing a dependency must not perform I/O before registering the pin");
                }
                finally { index.UnpinSnapshot(version); }
                index.SnapshotCount.Should().Be(0);
            }
            finally { writerField.SetValue(disk, writer); }
        }
    }
}
#endif
