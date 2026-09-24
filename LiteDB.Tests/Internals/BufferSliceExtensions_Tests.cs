using System;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using static LiteDB.Constants;

namespace LiteDB.Internals
{
    public class BufferSliceExtensions_Tests
    {
        [Fact]
        public void ReadGuid_FromRecycledFrame_FailsOwnershipCheck()
        {
            var page = new PageBuffer(new byte[PAGE_SIZE], 0, 1);
            var staleSlice = page.Slice(0, 16);
            page.Generation++;

            Action read = () => staleSlice.ReadGuid(0);

            read.Should().Throw<LiteException>()
                .WithMessage("*buffer slice belongs to a recycled cache frame*");
        }

        [Fact]
        public void WriteGuid_ToReadableFrame_FailsOwnershipCheck()
        {
            using var cache = new MemoryCache(new[] { 1 }, PAGE_SIZE);
            var page = cache.GetReadablePage(0, FileOrigin.Data, (_, __) => { });

            Action write = () => page.Write(Guid.Empty, 0);
            var exception = Record.Exception(write);

            page.Release();

            exception.Should().BeOfType<LiteException>()
                .Which.Message.Should().Contain("buffer slice is not owned by a writable cache frame");
        }
    }
}
