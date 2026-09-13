using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class AesLifecycle_Tests
    {
        [Fact]
        public void Constructor_LengthFailure_ClosesOwnedStream()
        {
            var stream = new TrackingStream { FailLength = true };
            Action create = () => new AesStream("password", stream);
            create.Should().Throw<IOException>().WithMessage("length failed");
            stream.DisposeCount.Should().Be(1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Dispose_ReleasesCryptoStreams_EvenWhenBaseDisposalFails(bool fail)
        {
            var stream = new TrackingStream();
            var encrypted = new AesStream("password", stream);
            var reader = GetCryptoStream(encrypted, "_reader");
            var writer = GetCryptoStream(encrypted, "_writer");
            stream.FailDispose = fail;
            Action dispose = () => encrypted.Dispose();
            if (fail) dispose.Should().Throw<AggregateException>();
            else dispose.Should().NotThrow();

            reader.CanRead.Should().BeFalse();
            writer.CanWrite.Should().BeFalse();
            encrypted.Dispose();
            stream.DisposeCount.Should().Be(1);
        }

        private static CryptoStream GetCryptoStream(AesStream stream, string field)
        {
            return (CryptoStream)typeof(AesStream).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(stream);
        }

        private sealed class TrackingStream : MemoryStream
        {
            public int DisposeCount { get; private set; }
            public bool FailDispose { get; set; }
            public bool FailLength { get; set; }
            public override long Length => this.FailLength ? throw new IOException("length failed") : base.Length;
            protected override void Dispose(bool disposing)
            {
                if (disposing) this.DisposeCount++;
                base.Dispose(disposing);
                if (this.FailDispose) throw new IOException("dispose failed");
            }
        }
    }
}
