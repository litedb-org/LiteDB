using System;
using System.Linq;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Internals
{
    public class BsonFieldNameCache_Tests
    {
        [Fact]
        public void Collisions_and_reused_source_bytes_do_not_change_field_names()
        {
            var cache = new BsonFieldNameCache();
            // Same length and endpoint bytes deliberately collide in the cache.
            var names = Enumerable.Range(0, 256).Select(i => "a" + i.ToString("D4") + "z")
                .Concat(new[] { "", "_id", "_ID", "payload", new string('x', 64), new string('x', 65), "straße", "İ", "日本語", "😀" }).ToArray();
            var source = new byte[256];
            for (var round = 0; round < 3; round++)
                foreach (var name in names)
                {
                    var count = Encoding.UTF8.GetBytes(name, 0, name.Length, source, 7);
                    var value = cache.Read(source, 7, count);
                    value.Should().Be(name);
                    Array.Clear(source, 0, source.Length);
                    value.Should().Be(name, "a cached string cannot borrow a mutable source buffer");
                }
            var bytes = Encoding.UTF8.GetBytes("payload");
            var first = cache.Read(bytes, 0, bytes.Length);
            cache.Read(bytes, 0, bytes.Length).Should().BeSameAs(first);
        }

        [Fact]
        public void Warm_cache_keeps_strict_utf8_validation()
        {
            var cache = new BsonFieldNameCache();
            foreach (var invalid in new[] { new byte[] { 0xff }, new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xc3 } })
            {
                var valid = Enumerable.Repeat((byte)'a', invalid.Length).ToArray();
                cache.Read(valid, 0, valid.Length).Should().Be(new string('a', valid.Length));
                Action decode = () => cache.Read(invalid, 0, invalid.Length);
                decode.Should().Throw<DecoderFallbackException>();
                cache.Read(valid, 0, valid.Length).Should().Be(new string('a', valid.Length));
            }
        }

        [Theory]
        [InlineData("payload")]
        [InlineData("straße😀")]
        [InlineData("")]
        public void Field_names_are_identical_when_the_name_or_terminator_crosses_a_segment(string name)
        {
            var bytes = Encoding.UTF8.GetBytes(name + "\0");
            var cache = new BsonFieldNameCache();
            cache.Read(bytes, 0, bytes.Length - 1).Should().Be(name);
            for (var split = 1; split < bytes.Length; split++)
            {
                using var reader = new BufferReader(new[]
                {
                    new BufferSlice(bytes, 0, split),
                    new BufferSlice(bytes, split, bytes.Length - split)
                }) { FieldNames = cache };
                reader.ReadCString().Should().Be(name);
                reader.Position.Should().Be(bytes.Length);
            }
            using var contiguous = new BufferReader(bytes) { FieldNames = cache };
            contiguous.ReadCString().Should().Be(name);
        }
    }
}
