using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Tests.QueryTest
{
    public partial class VectorIndex_Tests
    {
        [Theory]
        [InlineData(VectorIndexService.ReverseScanPageBudget, false)]
        [InlineData(VectorIndexService.ReverseScanPageBudget + 1, true)]
        public void Reverse_census_boundary_preserves_nodes_when_file_cannot_be_certified(int lastPage, bool deferred)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename)) BuildBoundedRepairGraph(db, 65, arms: false);
            ExtendWithSparseEmptyPages(file.Filename, (uint)lastPage);
            using (var db = new LiteDatabase(file.Filename))
            {
                db.BeginTrans().Should().BeTrue();
                db.GetCollection<VectorDocument>("docs").Delete(2).Should().BeTrue();
                RepairDeferred(db).Should().Be(deferred);
                AssertAllocatedVectorLinks(db, deferred ? 65 : 64);
                if (deferred)
                    InspectVectorIndex(db, "docs", (snapshot, collation, metadata) =>
                        snapshot.LocalPages.OfType<VectorIndexPage>().Count(x => x.IsDirty)).Should().Be(0);
                db.Commit().Should().BeTrue();
                db.Checkpoint();
            }
            using (var db = new LiteDatabase(file.Filename))
            {
                RepairDeferred(db).Should().Be(deferred);
                AssertAllocatedVectorLinks(db, deferred ? 65 : 64);
                db.GetCollection<VectorDocument>("docs").FindById(2).Should().BeNull();
                db.Rebuild();
                RepairDeferred(db).Should().BeFalse();
                AssertConnectivity(db, 64);
            }
        }

        // Valid checksummed free pages, stored sparsely: only the 32-byte headers
        // need writing because all free-page payloads are zero. No large document
        // workload or benchmark is required to reach the physical census limit.
        private static void ExtendWithSparseEmptyPages(string filename, uint lastPage)
        {
            using var stream = new FileStream(filename, FileMode.Open, FileAccess.ReadWrite);
            var headerBytes = new byte[PAGE_SIZE];
            stream.ReadRequired(headerBytes, 0, headerBytes.Length);
            var header = new HeaderPage(new PageBuffer(headerBytes, 0, 0));
            var oldLast = header.LastPageID;
            lastPage.Should().BeGreaterThan(oldLast);
            stream.SetLength(((long)lastPage + 1) * PAGE_SIZE);
            for (var id = oldLast + 1; id <= lastPage; id++)
            {
                var page = new BasePage(new PageBuffer(new byte[PAGE_SIZE], 0, 0), id, PageType.Empty)
                {
                    NextPageID = id == lastPage ? header.FreeEmptyPageList : id + 1
                };
                PageChecksum.Write(page.UpdateBuffer());
                stream.Position = (long)id * PAGE_SIZE;
                stream.Write(page.Buffer.Array, page.Buffer.Offset, PAGE_HEADER_SIZE);
            }
            header.FreeEmptyPageList = oldLast + 1;
            header.LastPageID = lastPage;
            PageChecksum.Write(header.UpdateBuffer());
            stream.Position = 0;
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.FlushToDisk();
        }
    }
}
