#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileAllocation_Tests
    {
        [OneFileTheory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public async Task Rollback_or_killed_allocator_preserves_free_lists_LastPageID_and_index_reachability(string password, bool killed)
        {
            using var files = new SharedOneFilePublicationFixture(password, legacy: true);
            using (var engine = new SharedEngine(files.Settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                db.CheckpointSize = 0;
                db.GetCollection("spare").Insert(Enumerable.Range(400, 60).Select(id => Row(id, 9)));
                db.DropCollection("spare");
                db.Checkpoint();
                var table = engine.MappedReaderTable;
                var structural = table.Authority.Map.Load(SharedMemoryLayout.StructuralEpoch);
                var reset = table.Authority.Map.Load(SharedMemoryLayout.ResetEpoch);
                if (killed)
                {
                    using var child = new MvccProcess("one-file-allocation-cut", files.Filename, password);
                    await child.Expect("ready");
                    await child.Kill();
                    table.Authority.Map.Load(SharedMemoryLayout.WriterOwner).Should().NotBe(0, "the dead writer left an unfinished tenure");
                }
                else
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("abort").Insert(Enumerable.Range(400, 60).Select(id => Row(id, 9)));
                    var transaction = Field<TransactionMonitor>(Core(engine), "_monitor").GetTransaction(false, false, out _);
                    transaction.Pages.NewPages.Should().NotBeEmpty();
                    db.Rollback().Should().BeTrue();
                }
                db.GetCollection("docs").Insert(Enumerable.Range(200, 30).Select(id => Row(id, 4)));
                db.Checkpoint();
                Quiet(table);
                table.Authority.Map.Load(SharedMemoryLayout.StructuralEpoch).Should().BeGreaterThan(structural);
                table.Authority.Map.Load(SharedMemoryLayout.ResetEpoch).Should().BeGreaterThanOrEqualTo(reset);
            }
            for (var recovery = 0; recovery < 2; recovery++)
            {
                // These independent persisted-page oracles validate LastPageID,
                // complete free-list ownership and reachability from all index roots.
                AllocationReachability.Verify(files.Filename, password);
                using var engine = new SharedEngine(files.Settings);
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("docs").FindAll().Should().BeEquivalentTo(
                    Enumerable.Range(1, 160).Select(id => Row(id, 0)).Concat(Enumerable.Range(200, 30).Select(id => Row(id, 4))));
                db.GetCollection("docs").Find(Query.EQ("value", 4)).Should().BeEquivalentTo(Enumerable.Range(200, 30).Select(id => Row(id, 4)));
                db.GetCollection("docs").Find(Query.EQ("value", 0)).Should().BeEquivalentTo(Enumerable.Range(1, 160).Select(id => Row(id, 0)));
                db.GetCollection("abort").Count().Should().Be(0);
                db.GetCollection("sentinel").FindAll().Should().BeEquivalentTo(new[] { Row(42, 9) });
                db.Checkpoint();
                Quiet(engine.MappedReaderTable);
            }
        }
    }
}
#endif
