using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class DocumentCacheEnumerable_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Dispose_Always_Closes_Source_And_Only_Drains_Groups(bool drain)
        {
            var disposed = false;
            var continued = false;
            IEnumerable<BsonDocument> Source()
            {
                try
                {
                    yield return new BsonDocument { RawId = new PageAddress(1, 0) };
                    continued = true;
                    throw new IOException("injected source failure");
                }
                finally
                {
                    disposed = true;
                }
            }

            using var cache = new DocumentCacheEnumerable(Source(), null, drainOnDispose: drain);
            using var cursor = cache.GetEnumerator();
            cursor.MoveNext().Should().BeTrue();
            Action close = cache.Dispose;
            if (drain) close.Should().Throw<IOException>().WithMessage("injected source failure");
            else close.Should().NotThrow();
            disposed.Should().BeTrue();
            continued.Should().Be(drain);
            close.Should().NotThrow();
        }
    }
}
