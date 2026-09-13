using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class FreePage_Tests
    {
        // FreeBytes ranges on page slot for free list page
        // 90% - 100% = 0 (7344 - 8160)
        // 75% -  90% = 1 (6120 - 7343)
        // 60% -  75% = 2 (4896 - 6119)
        // 30% -  60% = 3 (2448 - 4895)
        //  0% -  30% = 4 (0000 - 2447)

        [Fact]
        public void FreeSlot_Insert()
        {
            using (var e = new LiteEngine())
            {
                e.BeginTrans();

                // get transaction/snapshot "col1"
                var t = e.GetMonitor().GetTransaction(false, false, out var isNew);
                var s = t.CreateSnapshot(LockMode.Write, "col1", true);

                e.Insert("col1", new BsonDocument[] {new BsonDocument {["n"] = new byte[200]}}, BsonAutoId.Int32);

                // get pages
                var colPage = s.CollectionPage;
                var dataPage = s.LocalPages.FirstOrDefault(x => x.PageType == PageType.Data);
                var indexPage = s.LocalPages.FirstOrDefault(x => x.PageType == PageType.Index);

                // test dataPage free space
                dataPage.FreeBytes.Should().Be(7928);

                // page should be in Slot #0 (7344 - 8160 free bytes)
                colPage.FreeDataPageList.Should().Equal(dataPage.PageID, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue);

                // adding 1 more document into same page
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["n"] = new byte[600]}}, BsonAutoId.Int32);

                dataPage.FreeBytes.Should().Be(7296);

                // page should me moved into Slot #1 (6120 - 7343 free bytes)
                colPage.FreeDataPageList.Should().Equal(uint.MaxValue, dataPage.PageID, uint.MaxValue, uint.MaxValue, uint.MaxValue);

                // adding 1 big document to move this page into last page
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["n"] = new byte[6000]}}, BsonAutoId.Int32);

                dataPage.FreeBytes.Should().Be(1264);

                // now this page should me moved into last Slot (#4) - next document will use another data page (even a very small document)
                colPage.FreeDataPageList.Should().Equal(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, dataPage.PageID);

                // adding a very small document to test adding new page
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["n"] = new byte[10]}}, BsonAutoId.Int32);

                // no changes in dataPage... but new page as created
                dataPage.FreeBytes.Should().Be(1264);

                var dataPage2 = s.LocalPages.FirstOrDefault(x => x.PageType == PageType.Data && x.PageID != dataPage.PageID);

                dataPage2.FreeBytes.Should().Be(8118);

                // test slots (#0 for dataPage2 and #4 for dataPage1)
                colPage.FreeDataPageList.Should().Equal(dataPage2.PageID, uint.MaxValue, uint.MaxValue, uint.MaxValue, dataPage.PageID);

                // add another big document into dataPage2 do put both pages in same free Slot (#4)
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["n"] = new byte[7000]}}, BsonAutoId.Int32);

                // now, both pages are linked in same slot #4 (starts with new dataPage2)
                colPage.FreeDataPageList.Should().Equal(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, dataPage2.PageID);

                // dataPage2 link into dataPage1
                dataPage2.NextPageID.Should().Be(dataPage.PageID);
                dataPage.PrevPageID.Should().Be(dataPage2.PageID);

                // and both start/end points to null
                dataPage2.PrevPageID.Should().Be(uint.MaxValue);
                dataPage.NextPageID.Should().Be(uint.MaxValue);

                // do ColID tests
                dataPage.ColID.Should().Be(colPage.PageID);
                dataPage2.ColID.Should().Be(colPage.PageID);
                indexPage.ColID.Should().Be(colPage.PageID);
            }
        }

        [Fact]
        public void FreeSlot_Delete()
        {
            using (var e = new LiteEngine())
            {
                e.BeginTrans();

                // get transaction/snapshot "col1"
                var t = e.GetMonitor().GetTransaction(false, false, out var isNew);
                var s = t.CreateSnapshot(LockMode.Write, "col1", true);

                // first page
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["_id"] = 1, ["n"] = new byte[2000]}}, BsonAutoId.Int32);
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["_id"] = 2, ["n"] = new byte[2000]}}, BsonAutoId.Int32);
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["_id"] = 3, ["n"] = new byte[2000]}}, BsonAutoId.Int32);

                // second page
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["_id"] = 4, ["n"] = new byte[2000]}}, BsonAutoId.Int32);
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["_id"] = 5, ["n"] = new byte[2000]}}, BsonAutoId.Int32);
                e.Insert("col1", new BsonDocument[] {new BsonDocument {["_id"] = 6, ["n"] = new byte[2000]}}, BsonAutoId.Int32);

                // get pages
                var colPage = s.CollectionPage;
                var indexPage = s.LocalPages.FirstOrDefault(x => x.PageType == PageType.Index);
                var dataPage1 = s.LocalPages.FirstOrDefault(x => x.PageType == PageType.Data);
                var dataPage2 = s.LocalPages.FirstOrDefault(x => x.PageType == PageType.Data && x.PageID != dataPage1.PageID);

                // test dataPage free space
                dataPage1.FreeBytes.Should().Be(2064);
                dataPage2.FreeBytes.Should().Be(2064);

                colPage.FreeDataPageList.Should().Equal(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue, dataPage2.PageID);

                // delete some data
                e.Delete("col1", new BsonValue[] {2});

                // test again dataPage
                dataPage1.FreeBytes.Should().Be(4092);

                colPage.FreeDataPageList.Should().Equal(uint.MaxValue, uint.MaxValue, uint.MaxValue, dataPage1.PageID, dataPage2.PageID);

                // clear first page
                e.Delete("col1", new BsonValue[] {1, 3});

                // page1 must be now a clean page
                var emptyPage = s.LocalPages.FirstOrDefault(x => x.PageID == dataPage1.PageID);

                emptyPage.PageType.Should().Be(PageType.Empty);
                emptyPage.ItemsCount.Should().Be(0);
                emptyPage.FreeBytes.Should().Be(8160);

                t.Pages.DeletedPages.Should().Be(1);
                t.Pages.FirstDeletedPageID.Should().Be(emptyPage.PageID);
                t.Pages.LastDeletedPageID.Should().Be(emptyPage.PageID);
            }
        }
    }
}