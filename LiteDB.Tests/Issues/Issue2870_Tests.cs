using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using FluentAssertions;

using LiteDB.Engine;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2870_Tests
    {
        [Fact]
        public void Query_preserves_the_original_frame_when_the_first_source_read_throws()
        {
            using var engine = new LiteEngine();
            const string expectedMessage = "issue-2870-first-read";
            var expected = new InvalidOperationException(expectedMessage);
            var source = new ThrowingSource(expected, successfulReads: 0);

            engine.RegisterSystemCollection(
                "$issue2870_first",
                () => source);

            Action query = () => engine.Query("$issue2870_first", Query.All());

            var actual = query.Should().ThrowExactly<InvalidOperationException>().Which;

            source.MoveNextCalls.Should().Be(1);
            AssertOriginalExceptionPreserved(
                actual,
                expected,
                expectedMessage,
                nameof(ThrowAtOriginalSourceSite));
        }

        [Fact]
        public void Query_preserves_the_original_frame_when_a_later_source_read_throws()
        {
            using var engine = new LiteEngine();
            const string expectedMessage = "issue-2870-later-read";
            var expected = new InvalidOperationException(expectedMessage);
            var source = new ThrowingSource(expected, successfulReads: 1);

            engine.RegisterSystemCollection(
                "$issue2870_later",
                () => source);

            using var reader = engine.Query("$issue2870_later", Query.All());

            reader.HasValues.Should().BeTrue();
            reader.Read().Should().BeTrue();
            reader.Current.AsDocument["sequence"].AsInt32.Should().Be(1);

            Action readAgain = () => reader.Read();
            var actual = readAgain.Should().ThrowExactly<InvalidOperationException>().Which;

            source.MoveNextCalls.Should().Be(2);
            AssertOriginalExceptionPreserved(
                actual,
                expected,
                expectedMessage,
                nameof(ThrowAtOriginalSourceSite));
        }

        [Fact]
        public void Healthy_external_source_enumerates_every_document_through_query()
        {
            using var engine = new LiteEngine();
            var source = Enumerable.Range(1, 3)
                .Select(sequence => new BsonDocument { ["sequence"] = sequence })
                .ToArray();

            engine.RegisterSystemCollection("$issue2870_healthy", () => source);

            var actual = engine.Query("$issue2870_healthy", Query.All())
                .ToArray()
                .Select(value => value.AsDocument["sequence"].AsInt32);

            actual.Should().Equal(1, 2, 3);
        }

        [Fact]
        public void WaitIfLocked_preserves_the_original_frame_for_non_lock_errors()
        {
            const string expectedMessage = "issue-2870-non-lock-io";
            var expected = CaptureIOExceptionAtOriginalWaitSite(expectedMessage);

            expected.StackTrace.Should().Contain(nameof(ThrowAtOriginalWaitSite));

            Action wait = () => expected.WaitIfLocked(0);
            var actual = wait.Should().ThrowExactly<IOException>().Which;

            AssertOriginalExceptionPreserved(
                actual,
                expected,
                expectedMessage,
                nameof(ThrowAtOriginalWaitSite));
        }

        [Fact]
        public void WaitIfLocked_returns_normally_for_a_lock_error()
        {
            const int sharingViolationHResult = unchecked((int)0x80070020);
            var locked = new IOException("issue-2870-lock-control", sharingViolationHResult);

            locked.IsLocked().Should().BeTrue();
            Action wait = () => locked.WaitIfLocked(0);

            wait.Should().NotThrow();
        }

        [Fact]
        public void FileReaderV8_preserves_the_original_stream_failure_frame()
        {
            const string expectedMessage = "issue-2870-file-reader-io";
            var expected = new IOException(expectedMessage);
            using var data = new ThrowingReadStream(expected);
            using var log = new MemoryStream();
            var errors = new List<FileReaderError>();
            var settings = new EngineSettings { DataStream = data, LogStream = log };
            using var reader = new FileReaderV8(settings, errors);

            Action open = reader.Open;
            var actual = open.Should().ThrowExactly<IOException>().Which;

            data.ReadCalls.Should().Be(1);
            errors.Should().NotBeEmpty();
            errors.Should().OnlyContain(error => ReferenceEquals(error.Exception, expected));
            AssertOriginalExceptionPreserved(
                actual,
                expected,
                expectedMessage,
                nameof(ThrowAtOriginalFileReadSite));
        }

        [Fact]
        public void Healthy_FileReaderV8_reopens_persisted_documents()
        {
            using var data = new MemoryStream();
            using var log = new MemoryStream();
            var settings = new EngineSettings { DataStream = data, LogStream = log };

            using (var engine = new LiteEngine(settings))
            {
                engine.Insert(
                    "documents",
                    new[] { new BsonDocument { ["_id"] = 17, ["payload"] = "healthy" } },
                    BsonAutoId.Int32);
                engine.Checkpoint();
            }

            var errors = new List<FileReaderError>();
            using var reader = new FileReaderV8(settings, errors);

            reader.Open();

            reader.GetCollections().Should().ContainSingle().Which.Should().Be("documents");
            var actual = reader.GetDocuments("documents").Should().ContainSingle().Which;
            actual["_id"].AsInt32.Should().Be(17);
            actual["payload"].AsString.Should().Be("healthy");
            errors.Should().BeEmpty();
        }

        private static void AssertOriginalExceptionPreserved(
            Exception actual,
            Exception expected,
            string expectedMessage,
            string expectedFrame)
        {
            actual.Should().BeSameAs(expected);
            actual.Message.Should().Be(expectedMessage);
            actual.StackTrace.Should().Contain(expectedFrame);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowAtOriginalSourceSite(Exception exception)
        {
            throw exception;
        }

        private static IOException CaptureIOExceptionAtOriginalWaitSite(string message)
        {
            try
            {
                ThrowAtOriginalWaitSite(message);
                return null;
            }
            catch (IOException ex)
            {
                return ex;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowAtOriginalWaitSite(string message)
        {
            throw new IOException(message);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowAtOriginalFileReadSite(Exception exception)
        {
            throw exception;
        }

        private sealed class ThrowingSource : IEnumerable<BsonDocument>, IEnumerator<BsonDocument>
        {
            private readonly Exception _exception;
            private readonly int _successfulReads;

            public ThrowingSource(Exception exception, int successfulReads)
            {
                _exception = exception;
                _successfulReads = successfulReads;
            }

            public BsonDocument Current { get; private set; }

            public int MoveNextCalls { get; private set; }

            object IEnumerator.Current => this.Current;

            public IEnumerator<BsonDocument> GetEnumerator() => this;

            IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

            public bool MoveNext()
            {
                this.MoveNextCalls++;

                if (this.MoveNextCalls <= _successfulReads)
                {
                    this.Current = new BsonDocument { ["sequence"] = this.MoveNextCalls };
                    return true;
                }

                ThrowAtOriginalSourceSite(_exception);
                return false;
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }

        private sealed class ThrowingReadStream : MemoryStream
        {
            private readonly Exception _exception;

            public ThrowingReadStream(Exception exception)
                : base(new byte[Constants.PAGE_SIZE], writable: true)
            {
                _exception = exception;
            }

            public int ReadCalls { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                this.ReadCalls++;
                ThrowAtOriginalFileReadSite(_exception);
                return 0;
            }
        }
    }
}
