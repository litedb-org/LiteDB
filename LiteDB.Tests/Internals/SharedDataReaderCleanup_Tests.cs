using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using LiteDB.Client.Shared;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedDataReaderCleanup_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Owned_cleanup_releases_the_handle_and_callback_once_even_when_disposal_throws(bool readerFails, bool callbackFails)
        {
            var reader = new ThrowingReader(readerFails);
            var calls = 0;
            var cleanup = new SharedReaderCleanup(reader, () =>
            {
                calls++;
                if (callbackFails) throw new InvalidOperationException("release failed");
            });
            Action dispose = cleanup.Dispose;
            if (readerFails || callbackFails) dispose.Should().Throw<InvalidOperationException>();
            else dispose.Should().NotThrow();
            cleanup.Dispose();
            reader.Disposals.Should().Be(1);
            calls.Should().Be(1);
            typeof(SharedReaderCleanup).GetField("_root", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .GetValue(cleanup).As<GCHandle>().IsAllocated.Should().BeFalse();
        }

        private sealed class ThrowingReader : IBsonDataReader
        {
            private readonly bool _fails;
            internal int Disposals;
            internal ThrowingReader(bool fails) => _fails = fails;
            public BsonValue this[string field] => BsonValue.Null;
            public string Collection => "docs";
            public BsonValue Current => BsonValue.Null;
            public bool HasValues => false;
            public bool Read() => false;
            public void Dispose()
            {
                Disposals++;
                if (_fails) throw new InvalidOperationException("reader failed");
            }
        }
    }
}
