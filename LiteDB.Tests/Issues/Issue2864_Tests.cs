using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2864_Tests
    {
        [Theory]
        [InlineData(0, "Empty")]
        [InlineData(1, "Header")]
        [InlineData(3, "Index")]
        [InlineData(4, "Data")]
        [InlineData(5, "VectorIndex")]
        [InlineData(127, "127")]
        public void Collection_page_mismatch_reports_the_type_stored_in_the_page(byte rawType, string typeName)
        {
            var buffer = CreateBuffer(rawType);

            var exception = Assert.Throws<LiteException>(() => new CollectionPage(buffer));

            exception.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            exception.Message.Should().Be($"page type must be collection page, but it is {typeName}");
        }

        [Theory]
        [InlineData(0, "Empty")]
        [InlineData(1, "Header")]
        [InlineData(2, "Collection")]
        [InlineData(4, "Data")]
        [InlineData(5, "VectorIndex")]
        [InlineData(127, "127")]
        public void Index_page_mismatch_reports_the_type_stored_in_the_page(byte rawType, string typeName)
        {
            var buffer = CreateBuffer(rawType);

            var exception = Assert.Throws<LiteException>(() => new IndexPage(buffer));

            exception.ErrorCode.Should().Be(LiteException.INVALID_DATAFILE_STATE);
            exception.Message.Should().Be($"page type must be index page, but it is {typeName}");
        }

        [Theory]
        [InlineData((byte)PageType.Collection)]
        [InlineData((byte)PageType.Index)]
        public void Matching_page_type_is_accepted_and_preserves_the_raw_header(byte rawType)
        {
            var buffer = CreateBuffer(rawType);

            BasePage page = rawType == (byte)PageType.Collection
                ? new CollectionPage(buffer)
                : new IndexPage(buffer);

            page.PageType.Should().Be((PageType)rawType);
            page.Buffer.ReadByte(BasePage.P_PAGE_TYPE).Should().Be(rawType);
        }

        private static PageBuffer CreateBuffer(byte rawType)
        {
            var bytes = new byte[Constants.PAGE_SIZE];
            bytes[BasePage.P_PAGE_TYPE] = rawType;
            var buffer = new PageBuffer(bytes, 0, 2864);

            // This is the independent half of the diagnostic check: the fixture
            // proves the exact byte before either specialized page reader sees it.
            buffer.ReadByte(BasePage.P_PAGE_TYPE).Should().Be(rawType);

            return buffer;
        }
    }
}
