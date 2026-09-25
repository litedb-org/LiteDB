using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccSharedRegistry_Tests : IDisposable
    {
        private const int STREAMED_DOCUMENTS = 300;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-mvcc-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private string Leases => Filename + "-readers";

        public MvccSharedRegistry_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public void SmallResultCompletesWithoutLease_AndKeepsItsSnapshot()
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename });
            engine.Insert("docs", Documents(3, 0), BsonAutoId.Int32);
            using var reader = engine.Query("docs", new Query());
            Directory.Exists(Leases).Should().BeFalse();
            engine.Update("docs", Documents(3, 1));

            reader.HasValues.Should().BeTrue();
            reader.Collection.Should().Be("docs");
            Values(reader).Should().Equal(0, 0, 0);
            reader.Read().Should().BeFalse();
        }

        [Fact]
        public void EmptyResultHasNoValues()
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename });
            engine.Insert("docs", Documents(1, 0), BsonAutoId.Int32);
            using var reader = engine.Query("missing", new Query());
            reader.HasValues.Should().BeFalse();
            reader.Read().Should().BeFalse();
        }

        [Fact]
        public void FailureWhileBuffering_IsRaisedAfterTheRowsThatPrecededIt()
        {
            var failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(new LiteException(0, "row 3"));
            using var reader = new BufferedDataReader(new BsonValue[] { 1, 2 }, "docs", failure);

            reader.Read().Should().BeTrue();
            reader.Read().Should().BeTrue();
            reader.Current.AsInt32.Should().Be(2);
            Action next = () => reader.Read();
            next.Should().Throw<LiteException>().WithMessage("row 3");
        }

        [Fact]
        public void LargeResultStreamsFromLeasedSnapshot_AndRegistryIsRemovedAfterwards()
        {
            using (var engine = new SharedEngine(new EngineSettings { Filename = Filename }))
            {
                engine.Insert("docs", Documents(STREAMED_DOCUMENTS, 0), BsonAutoId.Int32);
                using (var reader = engine.Query("docs", new Query()))
                {
                    Directory.GetFiles(Leases, "*.lease").Should().HaveCount(1);
                    new SharedReaderRegistry(Filename).LiveVersions().Should().HaveCount(1);
                    engine.Update("docs", Documents(STREAMED_DOCUMENTS, 1));
                    Values(reader).Should().HaveCount(STREAMED_DOCUMENTS).And.OnlyContain(value => value == 0);
                }
                engine.Checkpoint();
                // The connection keeps its slot file for its next reader; it names no version.
                new SharedReaderRegistry(Filename).LiveVersions().Should().BeEmpty();
            }
            // The slot file closes with the connection; the next scan removes the empty registry.
            new SharedReaderRegistry(Filename).LiveVersions().Should().BeEmpty();
            Directory.Exists(Leases).Should().BeFalse();
        }

        [Fact]
        public void LargeResultWithReadTransform_ExecutesEachUserCallbackOnce()
        {
            var calls = new Dictionary<int, int>();
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = Filename,
                ReadTransform = (_, value) =>
                {
                    if (value is BsonDocument document && document.TryGetValue("_id", out var id))
                    {
                        calls.TryGetValue(id.AsInt32, out var count);
                        calls[id.AsInt32] = count + 1;
                    }
                    return value;
                }
            });
            engine.Insert("docs", Documents(STREAMED_DOCUMENTS, 0), BsonAutoId.Int32);

            using var reader = engine.Query("docs", new Query());
            Values(reader).Should().HaveCount(STREAMED_DOCUMENTS);
            calls.Should().HaveCount(STREAMED_DOCUMENTS);
            calls.Values.Should().OnlyContain(count => count == 1);
        }

        [Fact]
        public void UnreadableRegistrySkipsCheckpointWhileExternalReaderIsLive()
        {
            using var setup = new SharedEngine(new EngineSettings { Filename = Filename });
            setup.Pragma(Pragmas.CHECKPOINT, 0);
            setup.Insert("docs", Documents(STREAMED_DOCUMENTS, 0), BsonAutoId.Int32);
            using var readerEngine = new SharedEngine(new EngineSettings { Filename = Filename });
            using var reader = readerEngine.Query("docs", new Query());
            Directory.GetFiles(Leases, "*.lease").Should().HaveCount(1);

            using var writer = new SharedEngine(new EngineSettings
            {
                Filename = Filename,
                SharedReaderFiles = (_, __) => throw new UnauthorizedAccessException("injected registry failure")
            });
            writer.Update("docs", Documents(STREAMED_DOCUMENTS, 1));
            var log = FileHelper.GetLogFile(Filename);
            var before = new FileInfo(log).Length;

            writer.Checkpoint().Should().Be(0);
            new FileInfo(log).Length.Should().Be(before);
            Values(reader).Should().HaveCount(STREAMED_DOCUMENTS).And.OnlyContain(value => value == 0);
        }

        [Fact]
        public void LeaseThatCannotBeProbed_CountsAsLiveInsteadOfFailingTheCheckpoint()
        {
            Directory.CreateDirectory(Leases);
            var lease = Path.Combine(Leases, "7-" + Guid.NewGuid().ToString("N") + ".lease");
            File.WriteAllBytes(lease, new byte[0]);
            File.SetAttributes(lease, FileAttributes.ReadOnly);
            try
            {
                var versions = new SharedReaderRegistry(Filename).LiveVersions();
                // A privileged account may still open it; then it is simply dead.
                if (File.Exists(lease)) versions.Should().Equal(7);
                else versions.Should().BeEmpty();
            }
            finally
            {
                if (File.Exists(lease)) File.SetAttributes(lease, FileAttributes.Normal);
            }
        }

        /// <summary>
        /// One connection's readers share a slot file. A checkpoint of another connection must
        /// keep the frames of every leased version in it, not only the oldest: frames the newer
        /// reader needs are unreachable for the oldest one and for the current state.
        /// </summary>
        [Fact]
        public void ReadersOfOneConnectionAtDifferentVersionsEachKeepTheirFrames()
        {
            using var readers = new SharedEngine(new EngineSettings { Filename = Filename });
            using var writer = new SharedEngine(new EngineSettings { Filename = Filename });
            writer.Insert("docs", Documents(STREAMED_DOCUMENTS, 0), BsonAutoId.Int32);
            using var oldest = readers.Query("docs", new Query());
            writer.Update("docs", Documents(STREAMED_DOCUMENTS, 1));
            using var newer = readers.Query("docs", new Query());
            new SharedReaderRegistry(Filename).LiveVersions().Distinct().Should().HaveCount(2);
            Directory.GetFiles(Leases, "*.lease").Should().HaveCount(1);

            for (var value = 2; value <= 5; value++)
            {
                writer.Update("docs", Documents(STREAMED_DOCUMENTS, value));
                // Partial checkpoints reclaim what no leased version needs; later commits reuse it.
                writer.Checkpoint();
            }

            Values(newer).Should().HaveCount(STREAMED_DOCUMENTS).And.OnlyContain(value => value == 1);
            Values(oldest).Should().HaveCount(STREAMED_DOCUMENTS).And.OnlyContain(value => value == 0);
        }

        private static BsonDocument[] Documents(int count, int value) =>
            Enumerable.Range(1, count).Select(id => new BsonDocument { ["_id"] = id, ["value"] = value }).ToArray();

        private static int[] Values(IBsonDataReader reader)
        {
            var values = new System.Collections.Generic.List<int>();
            while (reader.Read()) values.Add(reader.Current["value"].AsInt32);
            return values.ToArray();
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
