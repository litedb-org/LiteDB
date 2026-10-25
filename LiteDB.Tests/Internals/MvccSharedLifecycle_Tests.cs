using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class MvccSharedLifecycle_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-mvcc-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public MvccSharedLifecycle_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SameSharedEngineSupportsOldAndNewReadersWithAutomaticCheckpoints(bool readOnly)
        {
            using var writer = new SharedEngine(new EngineSettings { Filename = Filename });
            writer.Pragma(Pragmas.CHECKPOINT, 1);
            writer.Insert("docs", Documents(0), BsonAutoId.Int32);
            using var readerEngine = new SharedEngine(new EngineSettings { Filename = Filename, ReadOnly = readOnly });
            using var old = readerEngine.Query("docs", new Query());
            for (var i = 1; i <= 10; i++)
            {
                writer.Update("docs", Documents(i));
                using var latest = readerEngine.Query("docs", new Query());
                while (latest.Read()) latest.Current["value"].AsInt32.Should().Be(i);
            }
            while (old.Read()) old.Current["value"].AsInt32.Should().Be(0);
            old.Dispose();
            writer.Checkpoint();
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();
        }

        [Fact]
        public void ExplicitTransactionsAndRepeatedBeginBalanceMutexOwnership()
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename });
            engine.Insert("docs", Documents(0), BsonAutoId.Int32);
            engine.BeginTrans().Should().BeTrue();
            engine.BeginTrans().Should().BeFalse();
            engine.Update("docs", Documents(1));
            using (var reader = engine.Query("docs", new Query()))
            {
                while (reader.Read()) reader.Current["value"].AsInt32.Should().Be(1);
            }
            engine.Commit().Should().BeTrue();
            MvccCheckpoint_Tests.RunThread(() =>
            {
                using var other = new SharedEngine(new EngineSettings { Filename = Filename });
                other.Update("docs", Documents(2));
            });
            engine.BeginTrans().Should().BeTrue();
            engine.Update("docs", Documents(3));
            engine.Rollback().Should().BeTrue();
            using var current = engine.Query("docs", new Query());
            while (current.Read()) current.Current["value"].AsInt32.Should().Be(2);
        }

        [Fact]
        public void QueryConstructionFailureReleasesEngineMutexAndLease()
        {
            using var engine = new SharedEngine(new EngineSettings
            {
                Filename = Filename,
                ReadTransform = (_, __) => throw new InvalidOperationException("transform failed")
            });
            engine.Insert("docs", Documents(0), BsonAutoId.Int32);
            Action query = () => engine.Query("docs", new Query());
            query.Should().Throw<InvalidOperationException>().WithMessage("transform failed");
            MvccCheckpoint_Tests.RunThread(() =>
            {
                using var other = new SharedEngine(new EngineSettings { Filename = Filename });
                other.Update("docs", Documents(1));
                other.Checkpoint();
            });
            new SharedReaderRegistry(Filename).OldestVersion().Should().BeNull();
        }

        [Fact]
        public void RebuildRejectsLiveReadersAndSucceedsAfterTheyClose()
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename });
            engine.Insert("docs", Documents(0), BsonAutoId.Int32);
            using var reader = engine.Query("docs", new Query());
            Action rebuild = () => engine.Rebuild(new RebuildOptions());
            rebuild.Should().Throw<LiteException>().WithMessage("*Close shared readers*");
            while (reader.Read()) reader.Current["value"].AsInt32.Should().Be(0);
            reader.Dispose();
            rebuild.Should().NotThrow();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public void TwoSortingReadersUseIndependentTemporaryStorage(string password)
        {
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password });
            engine.Insert("docs", Documents(0, 2048, 900), BsonAutoId.Int32);
            Query Sorted()
            {
                var query = new Query();
                query.OrderBy.Add(new QueryOrder("payload", Query.Descending));
                return query;
            }
            using var first = engine.Query("docs", Sorted());
            engine.Update("docs", Documents(1, 2048, 900));
            using var second = engine.Query("docs", Sorted());
            while (first.Read()) first.Current["value"].AsInt32.Should().Be(0);
            while (second.Read()) second.Current["value"].AsInt32.Should().Be(1);
        }

        private static BsonDocument[] Documents(int value, int count = 32, int payloadSize = 3000) => Enumerable.Range(0, count).Select(id =>
            new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string('x', payloadSize) }).ToArray();

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
