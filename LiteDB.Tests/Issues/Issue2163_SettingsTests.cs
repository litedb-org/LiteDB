using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    [CollectionDefinition("Issue2163WorkingDirectory", DisableParallelization = true)]
    public class Issue2163WorkingDirectoryCollection { }

    [Collection("Issue2163WorkingDirectory")]
    public class Issue2163_SettingsTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Mixed_stream_and_filename_settings_cannot_touch_an_owned_wal(bool shared)
        {
            using var file = new TempFile();
            using var owner = new LiteDatabase(file.Filename);
            owner.CheckpointSize = 0;
            owner.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var logPath = FileHelper.GetLogFile(file.Filename);
            var logBytes = TempFile.ReadAllBytesShared(logPath);
            using var stream = new MemoryStream();
            var settings = new EngineSettings { Filename = file.Filename, DataStream = stream };
            Action open = () =>
            {
                using ILiteEngine contender = shared ? (ILiteEngine)new SharedEngine(settings) : new LiteEngine(settings);
            };
            open.Should().Throw<ArgumentException>().WithMessage("*DataStream*Filename*");
            stream.Length.Should().Be(0);
            TempFile.ReadAllBytesShared(logPath).Should().Equal(logBytes);
            owner.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
            owner.Checkpoint();
            owner.GetCollection("rows").Count().Should().Be(2);
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Relative_paths_stay_bound_when_the_working_directory_changes(ConnectionType mode)
        {
            var originalDirectory = Environment.CurrentDirectory;
            var root = Path.Combine(Path.GetTempPath(), "litedb-cwd-" + Guid.NewGuid().ToString("N"));
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            try
            {
                foreach (var directory in new[] { first, second })
                {
                    using var setup = new LiteDatabase(Path.Combine(directory, "data.db"));
                    setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = directory });
                }
                using var other = new LiteDatabase(Path.Combine(second, "data.db"));
                other.GetCollection("rows").Count().Should().Be(1);
                Environment.CurrentDirectory = first;
                using var relative = new LiteDatabase(new ConnectionString { Filename = "data.db", Connection = mode });
                relative.GetCollection("rows").Count().Should().Be(1);
                Environment.CurrentDirectory = second;
                relative.Rebuild();
                relative.GetCollection("rows").FindById(1)["value"].AsString.Should().Be(first);
                other.GetCollection("rows").FindById(1)["value"].AsString.Should().Be(second);
            }
            finally
            {
                Environment.CurrentDirectory = originalDirectory;
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(ConnectionType.Direct)]
        [InlineData(ConnectionType.Shared)]
        public void Mutating_caller_settings_cannot_turn_a_read_only_connection_into_a_writer(ConnectionType mode)
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
                setup.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            var settings = new EngineSettings { Filename = file.Filename, ReadOnly = true };
            using var engine = mode == ConnectionType.Direct ? (ILiteEngine)new LiteEngine(settings) : new SharedEngine(settings);
            using var db = new LiteDatabase(engine);
            db.GetCollection("rows").Count().Should().Be(1);
            using var parallel = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });
            parallel.GetCollection("rows").Count().Should().Be(1);
            settings.ReadOnly = false;
            settings.Filename = file.Filename + ".other";
            Action rebuild = () => db.Rebuild();
            rebuild.Should().Throw<LiteException>().WithMessage("*read-only*");
            db.GetCollection("rows").Count().Should().Be(1);
            parallel.GetCollection("rows").Count().Should().Be(1);
            File.Exists(settings.Filename).Should().BeFalse();
        }
    }
}
