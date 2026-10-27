#if NET8_0_OR_GREATER
using System;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileProductionReuse_Tests
    {
        [OneFileTheory]
        [InlineData("BootstrapStage:envelope-b")]
        [InlineData("BootstrapStage:before-storage")]
        [InlineData("BootstrapStage:after-storage")]
        [InlineData("BootstrapStage:before-ready")]
        [InlineData("BootstrapStage:ready")]
        [InlineData("BootstrapStage:before-dms-downgrade")]
        [InlineData("BootstrapStage:downgraded")]
        [InlineData("BootstrapStage:write-released")]
        [InlineData("BootstrapStage:claimed")]
        [InlineData("BootstrapStage:before-close")]
        [InlineData("BootstrapStage:closed")]
        [InlineData("PublicationStage:fence-begin")]
        [InlineData("PublicationStage:writer-epoch")]
        [InlineData("PublicationStage:writer-owner")]
        [InlineData("PublicationStage:recovery-owner")]
        [InlineData("PublicationStage:writer-begin")]
        [InlineData("PublicationStage:open-begin")]
        [InlineData("PublicationStage:open-structural-repaired")]
        [InlineData("PublicationStage:open-version")]
        [InlineData("PublicationStage:fast-admission-disabled")]
        [InlineData("PublicationStage:writer-uncertain")]
        [InlineData("PublicationStage:version-reset")]
        [InlineData("PublicationStage:recovery-cleared")]
        [InlineData("PublicationStage:fast-admission-enabled")]
        [InlineData("PublicationStage:fence-end")]
        [InlineData("PublicationStage:open-end")]
        [InlineData("PublicationStage:writer-end")]
        [InlineData("PublicationStage:write-released")]
        public async Task Actual_production_reuse_survives_two_deaths_at_each_store_and_handoff(string cut)
        {
            using var files = new SharedOneFilePublicationFixture();
            Guid authority;
            using (var bootstrap = new SharedEngine(files.Settings))
                authority = bootstrap.MappedReaderTable.Authority.Header.Authority;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                if (cut == "PublicationStage:version-reset" || cut == "PublicationStage:open-structural-repaired")
                {
                    using var map = new System.IO.FileStream(files.Filename + "-shm", System.IO.FileMode.Open,
                        System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite);
                    map.Position = cut.EndsWith("version-reset") ? SharedMemoryLayout.CommittedVersion : SharedMemoryLayout.StructuralEpoch;
                    var word = BitConverter.GetBytes(cut.EndsWith("version-reset") ? 999L : 3L);
                    map.Write(word, 0, word.Length); map.Flush(true);
                }
                using var child = new MvccProcess("one-file-production-reuse-cut", files.Filename, null, cut);
                await child.Expect("ready");
                await child.Kill();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(files.Settings);
                engine.MappedReaderTable.Authority.Header.Authority.Should().Be(authority);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                Verify(db, 0);
                db.BeginTrans().Should().BeTrue();
                db.Rollback().Should().BeTrue();
                Quiet(engine.MappedReaderTable);
                files.WriteIsFree();
            }
        }
    }
}
#endif
