using System;

using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class PageType_Tests
    {
        [Fact]
        public void CollectionPage_InvalidType_MessageIncludesActualType()
        {
            var buffer = CreateDataPageBuffer();

            Action read = () => new CollectionPage(buffer);

            read.Should().Throw<LiteException>()
                .WithMessage("page type must be collection page, but it is Data");
        }

        [Fact]
        public void IndexPage_InvalidType_MessageIncludesActualType()
        {
            var buffer = CreateDataPageBuffer();

            Action read = () => new IndexPage(buffer);

            read.Should().Throw<LiteException>()
                .WithMessage("page type must be index page, but it is Data");
        }

        private static PageBuffer CreateDataPageBuffer()
        {
            var buffer = new PageBuffer(new byte[Constants.PAGE_SIZE], 0, 0);
            buffer.Write((byte)PageType.Data, BasePage.P_PAGE_TYPE);
            return buffer;
        }
    }
}
