using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class WalFileAllocation_Tests
    {
        [Fact]
        public void Reserving_extents_preserves_length_bytes_and_truncation()
        {
            using var temporary = new TempFile();
            var first = new byte[] { 4, 8, 15, 16, 23, 42 };
            var last = new byte[] { 99, 17, 6 };
            using (var stream = new FileStream(temporary.Filename, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                var allocation = new WalFileAllocation();
                stream.Write(first, 0, first.Length);
                stream.Flush();
                allocation.Reserve(stream, WalFileAllocation.ChunkSize * 2L);
                stream.Length.Should().Be(first.Length, "reserved capacity is never a logical WAL tail");
                stream.Position = 0;
                var actual = new byte[first.Length];
                stream.ReadRequired(actual, 0, actual.Length);
                actual.Should().Equal(first);
                stream.SetLength(0);
                allocation.Reset();
                allocation.Reserve(stream, WalFileAllocation.ChunkSize + 1L);
                stream.Length.Should().Be(0);
                stream.Write(last, 0, last.Length);
                stream.Flush(true);
            }
            File.ReadAllBytes(temporary.Filename).Should().Equal(last);
        }

        [Fact]
        public void Caller_streams_keep_their_existing_length_and_write_contract()
        {
            using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
            var allocation = new WalFileAllocation();
            allocation.Reserve(stream, WalFileAllocation.ChunkSize);
            stream.ToArray().Should().Equal(1, 2, 3);
            stream.Length.Should().Be(3);
        }
    }
}
