using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class HeaderPage_Tests
    {
        [Fact]
        public void HeaderPage_Collections()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 1);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new header page
            var header = new HeaderPage(buffer, 0);

            header.InsertCollection("my-col1", 1);
            header.InsertCollection("my-col2", 2);

            header.GetCollections().Count().Should().Be(2);
            ((int) header.GetCollectionPageID("my-col1")).Should().Be(1);
            ((int) header.GetCollectionPageID("my-col2")).Should().Be(2);
            header.GetAvailableCollectionSpace().Should().Be(7955);

            header.UpdateBuffer();

            // read header
            var h2 = new HeaderPage(buffer);

            h2.GetCollections().Count().Should().Be(2);
            ((int) h2.GetCollectionPageID("my-col1")).Should().Be(1);
            ((int) h2.GetCollectionPageID("my-col2")).Should().Be(2);
            h2.GetAvailableCollectionSpace().Should().Be(7955);

            buffer.ShareCounter = 0;
        }

        [Fact]
        public void HeaderPage_Savepoint()
        {
            var data = new byte[Constants.PAGE_SIZE];
            var buffer = new PageBuffer(data, 0, 1);

            // mark buffer as writable (debug propose)
            buffer.ShareCounter = Constants.BUFFER_WRITABLE;

            // create new header page
            var header = new HeaderPage(buffer, 0);

            header.InsertCollection("my-col1", 1);
            header.InsertCollection("my-col2", 2);

            header.GetCollections().Count().Should().Be(2);

            // savepoint alse execute UpdateBuffer
            var sp = header.Savepoint();

            header.DeleteCollection("my-col1");
            header.DeleteCollection("my-col2");

            header.UpdateBuffer();

            header.GetCollections().Count().Should().Be(0);

            // now, restore header
            header.Restore(sp);

            header.GetCollections().Count().Should().Be(2);

            buffer.ShareCounter = 0;
        }
    }
}