using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1344_Tests
    {
        private const string TargetId = "$/tweet/image.jpg";

        private sealed class CoordinatedReadStream : Stream
        {
            private readonly MemoryStream _inner;
            private readonly Action _beforeFirstRead;
            private bool _firstRead = true;

            public CoordinatedReadStream(byte[] bytes, Action beforeFirstRead)
            {
                _inner = new MemoryStream(bytes, false);
                _beforeFirstRead = beforeFirstRead;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_firstRead)
                {
                    _firstRead = false;
                    _beforeFirstRead();
                }

                return _inner.Read(buffer, offset, count);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class ExpectedFile
        {
            public string Filename { get; set; }
            public byte[] Bytes { get; set; }
        }

        [Fact]
        public async Task Overlapping_uploads_to_one_id_commit_the_last_complete_payload_without_orphan_chunks()
        {
            using var file = new TempFile();
            var sentinel = CreatePayload(17, LiteFileStream<string>.MAX_CHUNK_SIZE + 37);
            var first = CreatePayload(41, LiteFileStream<string>.MAX_CHUNK_SIZE * 2 + 113);
            var replacement = CreatePayload(89, LiteFileStream<string>.MAX_CHUNK_SIZE * 3 + 257);
            Exception firstFailure = null;
            Exception replacementFailure = null;

            using (new AssertionScope())
            {
                using (var db = new LiteDatabase(file.Filename))
                using (var firstReachedCopy = new ManualResetEventSlim())
                using (var replacementReachedCopy = new ManualResetEventSlim())
                using (var firstCompleted = new ManualResetEventSlim())
                {
                    using (var sentinelSource = new MemoryStream(sentinel))
                    {
                        db.FileStorage.Upload("sentinel", "sentinel.bin", sentinelSource);
                    }

                    var firstTask = Task.Run(() =>
                    {
                        try
                        {
                            using var source = new CoordinatedReadStream(first, () =>
                            {
                                firstReachedCopy.Set();
                                replacementReachedCopy.Wait(TimeSpan.FromSeconds(2));
                            });
                            db.FileStorage.Upload(TargetId, "first.jpg", source);
                        }
                        catch (Exception ex)
                        {
                            firstFailure = ex;
                        }
                        finally
                        {
                            firstCompleted.Set();
                        }
                    });

                    firstReachedCopy.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

                    var replacementTask = Task.Run(() =>
                    {
                        try
                        {
                            using var source = new CoordinatedReadStream(replacement, () =>
                            {
                                replacementReachedCopy.Set();
                                firstCompleted.Wait(TimeSpan.FromSeconds(10));
                            });
                            db.FileStorage.Upload(TargetId, "replacement.jpg", source);
                        }
                        catch (Exception ex)
                        {
                            replacementFailure = ex;
                        }
                    });

                    var allTasks = Task.WhenAll(firstTask, replacementTask);
                    var completed = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(30)));
                    if (completed != allTasks) throw new TimeoutException("Overlapping uploads did not finish within 30 seconds.");
                    await allTasks;

                    AssertStorageLedger(db, sentinel, replacement);
                    firstFailure.Should().BeNull("the initial upload was acknowledged");
                    replacementFailure.Should().BeNull("a thread-safe overlapping upload must not leak a duplicate chunk key");
                }

                using (var reopened = new LiteDatabase(file.Filename))
                {
                    AssertStorageLedger(reopened, sentinel, replacement);
                }
            }
        }

        private static void AssertStorageLedger(LiteDatabase db, byte[] sentinel, byte[] replacement)
        {
            var expected = new Dictionary<string, ExpectedFile>
            {
                ["sentinel"] = new ExpectedFile { Filename = "sentinel.bin", Bytes = sentinel },
                [TargetId] = new ExpectedFile { Filename = "replacement.jpg", Bytes = replacement }
            };
            var files = db.GetCollection("_files").FindAll().ToArray();
            var chunks = db.GetCollection("_chunks").FindAll().ToArray();

            files.Select(x => x["_id"].AsString).OrderBy(x => x)
                .Should().Equal(expected.Keys.OrderBy(x => x));
            chunks.Select(x => x["_id"].AsDocument["f"].AsString)
                .Should().OnlyContain(owner => expected.ContainsKey(owner), "orphan chunks have no file owner");

            foreach (var pair in expected)
            {
                var file = files.SingleOrDefault(x => x["_id"].AsString == pair.Key);
                ((object)file).Should().NotBeNull();
                if (file == null) continue;

                var ownedChunks = chunks
                    .Where(x => x["_id"].AsDocument["f"].AsString == pair.Key)
                    .OrderBy(x => x["_id"].AsDocument["n"].AsInt32)
                    .ToArray();
                // CopyTo/Flush can persist a partial chunk before the final one.
                // Validate the stored sequence and bytes, not a fixed-width layout.
                var expectedChunkCount = ownedChunks.Length;
                ownedChunks.Should().NotBeEmpty();
                ownedChunks.Select(x => x["data"].AsBinary.Length)
                    .Should().OnlyContain(length => length > 0 && length <= LiteFileStream<string>.MAX_CHUNK_SIZE);

                file["filename"].AsString.Should().Be(pair.Value.Filename);
                file["length"].AsInt64.Should().Be(pair.Value.Bytes.Length);
                file["chunks"].AsInt32.Should().Be(expectedChunkCount);
                ownedChunks.Select(x => x["_id"].AsDocument["n"].AsInt32)
                    .Should().Equal(Enumerable.Range(0, expectedChunkCount));
                ownedChunks.SelectMany(x => x["data"].AsBinary)
                    .Should().Equal(pair.Value.Bytes, "raw chunks must agree with the file record");

                using var downloaded = new MemoryStream();
                db.FileStorage.Download(pair.Key, downloaded);
                downloaded.ToArray().Should().Equal(pair.Value.Bytes);
                db.FileStorage.FindById(pair.Key).Length.Should().Be(pair.Value.Bytes.Length);
            }
        }

        private static byte[] CreatePayload(int seed, int length)
        {
            return Enumerable.Range(0, length).Select(i => (byte)((i * 31 + seed) % 251)).ToArray();
        }
    }
}
