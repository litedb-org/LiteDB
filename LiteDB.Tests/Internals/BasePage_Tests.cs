using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class BasePage_Tests
    {
        [Fact]
        public void BasePage_Insert()
        {
            // create new memory area with 10 bytes offset (just for fun)
            var data = new byte[Constants.PAGE_SIZE + 10];
            var buffer = new PageBuffer(data, 10, 1);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new base page
            var page = new BasePage(buffer, 1, PageType.Empty);

            page.Insert(10, out var index0).Fill(1);
            page.Insert(20, out var index1).Fill(2);
            page.Insert(30, out var index2).Fill(3);
            page.Insert(40, out var index3).Fill(4);

            page.FragmentedBytes.Should().Be(0);
            page.UsedBytes.Should().Be(100);
            page.NextFreePosition.Should().Be(32 + 100);
            page.FooterSize.Should().Be(4 * 4);
            page.FreeBytes.Should().Be(8192 - 32 - 100 - (4 * 4));

            page.Get(index0).All(1).Should().BeTrue();
            page.Get(index1).All(2).Should().BeTrue();
            page.Get(index2).All(3).Should().BeTrue();
            page.Get(index3).All(4).Should().BeTrue();

            // update header buffer
            page.UpdateBuffer();

            // let's create another page instance based on same page buffer
            var page2 = new BasePage(buffer);

            ((int) page.PageID).Should().Be(1);
            page.PageType.Should().Be(PageType.Empty);

            page.Get(index0).All(1).Should().BeTrue();
            page.Get(index1).All(2).Should().BeTrue();
            page.Get(index2).All(3).Should().BeTrue();
            page.Get(index3).All(4).Should().BeTrue();

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Insert_Full_Bytes_Page()
        {
            // create new memory area
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 1);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new base page
            var page = new BasePage(buffer, 1, PageType.Empty);

            var full = page.FreeBytes - BasePage.SLOT_SIZE;

            page.Insert((ushort) full, out var index0).Fill(1);

            page.ItemsCount.Should().Be(1);
            ((int) page.UsedBytes).Should().Be(full);

            page.FreeBytes.Should().Be(0);
            ((int) page.NextFreePosition).Should().Be(32 + full);

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Insert_Full_Items_Page()
        {
            // create new memory area
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 1);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new base page
            var page = new BasePage(buffer, 1, PageType.Empty);

            // create 255 page segments
            for (byte i = 0; i < byte.MaxValue; i++)
            {
                page.Insert(10, out var index).Fill(i);
            }

            page.ItemsCount.Should().Be(255);
            page.UsedBytes.Should().Be(2550);
            page.FreeBytes.Should().Be(0);
            page.NextFreePosition.Should().Be(32 + 2550);

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Delete()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 0);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new base page
            var page = new BasePage(buffer, 1, PageType.Empty);

            var seg0 = page.Insert(100, out var index0);
            var seg1 = page.Insert(200, out var index1);
            var seg2 = page.Insert(300, out var index2);

            page.HighestIndex.Should().Be(2);
            page.ItemsCount.Should().Be(3);
            page.UsedBytes.Should().Be(600);
            page.NextFreePosition.Should().Be(32 + 600);
            page.FreeBytes.Should().Be(8192 - 32 - 12 - 600); // page size - header - footer - used
            page.FragmentedBytes.Should().Be(0);

            // deleting 300b (end of page)
            page.Delete(index2);

            page.HighestIndex.Should().Be(1);
            page.ItemsCount.Should().Be(2);
            page.UsedBytes.Should().Be(300);
            page.NextFreePosition.Should().Be(32 + 300);
            page.FreeBytes.Should().Be(8192 - 32 - 8 - 300);
            page.FragmentedBytes.Should().Be(0);

            // deleting 100b (middle of page) - create data fragment
            page.Delete(index0);

            page.HighestIndex.Should().Be(1);
            page.ItemsCount.Should().Be(1);
            page.UsedBytes.Should().Be(200);
            page.NextFreePosition.Should().Be(32 + 300); // 200 + 100 (fragmented)
            page.FreeBytes.Should().Be(8192 - 32 - 8 - 200);
            page.FragmentedBytes.Should().Be(100);

            // delete 200b - last item
            page.Delete(index1);

            // after delete last item page will be defrag

            page.HighestIndex.Should().Be(byte.MaxValue);
            page.ItemsCount.Should().Be(0);
            page.UsedBytes.Should().Be(0);
            page.NextFreePosition.Should().Be(32);
            page.FreeBytes.Should().Be(8192 - 32);
            page.FragmentedBytes.Should().Be(0);

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Delete_Full()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 0);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new base page
            var page = new BasePage(buffer, 1, PageType.Empty);

            var seg0 = page.Insert(100, out var index0);
            var seg1 = page.Insert(200, out var index1);
            var seg2 = page.Insert(8192 - 32 - (100 + 200 + 8) - 4, out var index2); // 7848

            seg0.Fill(10);
            seg1.Fill(11);
            seg2.Fill(12);

            page.HighestIndex.Should().Be(2);
            page.ItemsCount.Should().Be(3);
            page.UsedBytes.Should().Be(8148);
            page.NextFreePosition.Should().Be(8180); // no next free position
            page.FreeBytes.Should().Be(0); // full used
            page.FragmentedBytes.Should().Be(0);

            // deleting 200b (end of page)
            page.Delete(index1);

            page.HighestIndex.Should().Be(2);
            page.ItemsCount.Should().Be(2);
            page.UsedBytes.Should().Be(8148 - 200);
            page.NextFreePosition.Should().Be(8180);
            page.FreeBytes.Should().Be(200);
            page.FragmentedBytes.Should().Be(200);

            page.Delete(index0);

            page.HighestIndex.Should().Be(2);
            page.ItemsCount.Should().Be(1);
            page.UsedBytes.Should().Be(8148 - 200 - 100);
            page.NextFreePosition.Should().Be(8180);
            page.FreeBytes.Should().Be(300);
            page.FragmentedBytes.Should().Be(300);

            var seg3 = page.Insert(250, out var index3);

            seg3.Fill(13);

            page.HighestIndex.Should().Be(2);
            page.ItemsCount.Should().Be(2);
            page.UsedBytes.Should().Be(8148 - 200 - 100 + 250);
            page.NextFreePosition.Should().Be(8180 - 50);
            page.FreeBytes.Should().Be(50);
            page.FragmentedBytes.Should().Be(0);

            var seg3f = page.Get(index3);

            seg3f.All(13).Should().BeTrue();



            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Defrag()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 0);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            var page = new BasePage(buffer, 1, PageType.Empty);

            page.Insert(100, out var index0).Fill(101);
            page.Insert(200, out var index1).Fill(102);
            page.Insert(300, out var index2).Fill(103);
            page.Insert(400, out var index3).Fill(104);

            page.FragmentedBytes.Should().Be(0);
            page.UsedBytes.Should().Be(1000);
            page.NextFreePosition.Should().Be(32 + 1000);

            page.Delete(index0);
            page.Delete(index1);

            page.FragmentedBytes.Should().Be(300);
            page.UsedBytes.Should().Be(700);
            page.NextFreePosition.Should().Be(32 + 1000);

            // fill all page
            page.Insert(7440, out var index4).Fill(105); // 8192 - 32 - (4 * 4) - 700

            page.FragmentedBytes.Should().Be(0);
            page.UsedBytes.Should().Be(8140);
            page.NextFreePosition.Should().Be(8172);

            page.Get(index2).All(103).Should().BeTrue();
            page.Get(index3).All(104).Should().BeTrue();
            page.Get(index4).All(105).Should().BeTrue();

            page.GetUsedIndexs().ToArray().Should().Equal(0, 2, 3);

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Update()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 0);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            var page = new BasePage(buffer, 1, PageType.Empty);

            page.Insert(100, out var index0).Fill(101);
            page.Insert(200, out var index1).Fill(102);
            page.Insert(300, out var index2).Fill(103);
            page.Insert(400, out var index3).Fill(104);

            page.FragmentedBytes.Should().Be(0);
            page.UsedBytes.Should().Be(1000);
            page.NextFreePosition.Should().Be(32 + 1000);

            // update same segment length 
            page.Update(index0, 100).Fill(201);

            page.Get(index0).All(201).Should().BeTrue();
            page.FragmentedBytes.Should().Be(0);
            page.UsedBytes.Should().Be(1000);
            page.NextFreePosition.Should().Be(32 + 1000);

            // less bytes (segment in middle of page)
            page.Update(index1, 150).Fill(202);

            page.Get(index1).All(202).Should().BeTrue();
            page.FragmentedBytes.Should().Be(50);
            page.UsedBytes.Should().Be(950);
            page.NextFreePosition.Should().Be(32 + 1000);

            // less bytes (segment in end of page)
            page.Update(index3, 350).Fill(204);

            page.Get(index3).All(204).Should().BeTrue();
            page.FragmentedBytes.Should().Be(50);
            page.UsedBytes.Should().Be(900);
            page.NextFreePosition.Should().Be(32 + 950);

            // more bytes (segment in end of page)
            page.Update(index3, 550).Fill(214);

            page.Get(index3).All(214).Should().BeTrue();
            page.FragmentedBytes.Should().Be(50);
            page.UsedBytes.Should().Be(1100);
            page.NextFreePosition.Should().Be(32 + 1150);

            // more bytes (segment in middle of page)
            page.Update(index0, 200).Fill(211);

            page.Get(index0).All(211).Should().BeTrue();
            page.FragmentedBytes.Should().Be(150);
            page.UsedBytes.Should().Be(1200);
            page.NextFreePosition.Should().Be(32 + 1350);

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void BasePage_Test_Output()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 0);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            var page = new BasePage(buffer, 1, PageType.Empty);

            page.Insert(100, out var index0).Fill(101);
            page.Insert(7900, out var index1).Fill(102);
            page.Insert(100, out var index2).Fill(103);

            page.FragmentedBytes.Should().Be(0);
            page.UsedBytes.Should().Be(8100);
            page.NextFreePosition.Should().Be(8132);

            page.Delete(index1);

            page.Insert(7948 - 4, out var index3).Fill(104);

            buffer.ShareCounter = 0;
        }


    }
}