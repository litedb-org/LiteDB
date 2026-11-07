#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmPublication_Tests
    {
        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_publication_step_admits_only_a_sync_covered_version_and_stopped_writer_never_reenables(bool fail)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            var table = engine.MappedReaderTable;
            var state = State(Core(engine));
            var initial = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            var synced = false;
            var reached = new HashSet<string>();
            state.AtCrashPoint = stage =>
            {
                if (stage == "wal-before-durable-flush")
                {
                    table.Reads.TryRead(out var before).Should().BeTrue();
                    before.Version.Should().Be(initial);
                    if (fail) throw new System.IO.IOException("warm barrier failed");
                }
                if (stage == "wal-after-durable-flush") synced = true;
            };
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                reached.Add(stage);
                if (table.Reads.TryRead(out var status) && status.Version > initial)
                    synced.Should().BeTrue("mapped version must never lead its covering sync at " + stage);
                if (stage == "commit-version") table.Reads.TryRead(out _).Should().BeFalse();
            };
            try
            {
                db.GetCollection("docs").Update(Row(1, 1));
                Action commit = () => db.Commit();
                if (fail) commit.Should().Throw<System.IO.IOException>(); else commit();
                if (fail)
                {
                    synced.Should().BeFalse();
                    reached.Should().NotContain("commit-version");
                    table.Reads.TryRead(out _).Should().BeFalse();
                }
                else
                {
                    synced.Should().BeTrue();
                    reached.Should().Contain(new[] { "fence-begin", "commit-version", "fence-end", "writer-end" });
                    table.Reads.TryRead(out var after).Should().BeTrue();
                    after.Version.Should().BeGreaterThan(initial);
                }
            }
            finally { SharedMemoryReaderTable.PublicationStage = null; state.AtCrashPoint = null; }
            if (fail)
            {
                for (var i = 0; i < 2; i++) { db.Timeout.Should().BeGreaterThan(TimeSpan.Zero); Verify(db, 1); }
            }
        }
    }
}
#endif
