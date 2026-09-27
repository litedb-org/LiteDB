using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    [Collection(NativeFileSyncCollection.Name)]
    public class NativeFileSyncOverride_Tests
    {
        [Fact]
        public void Inherited_flush_override_preserves_failures_on_cold_and_cached_lookup()
        {
            using var file = new TempFile();
            using (var stream = new DerivedStream(file.Filename))
            {
                stream.WriteByte(42);
                stream.FlushToDisk();
                stream.DurableCalls.Should().Be(1);
                stream.Fail = true;
                Action flush = () => stream.FlushToDisk();
                flush.Should().Throw<IOException>().WithMessage("custom durable flush failed");
                stream.DurableCalls.Should().Be(2);
                stream.Fail = false;
            }
            using (var stream = new DerivedStream(file.Filename) { Fail = true })
            {
                Action flush = () => stream.FlushToDisk();
                flush.Should().Throw<IOException>().WithMessage("custom durable flush failed");
                stream.DurableCalls.Should().Be(1);
                stream.Fail = false;
            }
            File.ReadAllBytes(file.Filename).Should().Equal(new byte[] { 42 });
        }

        private class OverrideStream : FileStream
        {
            internal bool Fail;
            internal int DurableCalls;

            internal OverrideStream(string path) : base(path, FileMode.OpenOrCreate, FileAccess.ReadWrite) { }

            public override void Flush(bool flushToDisk)
            {
                if (flushToDisk)
                {
                    DurableCalls++;
                    if (Fail) throw new IOException("custom durable flush failed");
                }
                base.Flush(flushToDisk);
            }
        }

        private sealed class DerivedStream : OverrideStream
        {
            internal DerivedStream(string path) : base(path) { }
        }
    }
}
