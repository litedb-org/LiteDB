using System;
using System.IO;
using System.Linq;
using LiteDB.Engine;
using FluentAssertions.Execution;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2815_Tests
    {
        [Fact]
        public void Readonly_close_does_not_attempt_checkpoint_or_modify_live_writer_files()
        {
            using var file = new TempFile();
            using var writer = new LiteDatabase(file.Filename);
            writer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed in WAL" });
            var dataBefore = ReadShared(file.Filename);
            var logPath = Path.ChangeExtension(file.Filename, null) + "-log.db";
            var logBefore = ReadShared(logPath);
            Action openReader = () =>
            {
                using var reader = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true });
                reader.GetCollection("rows").FindAll().ToArray();
            };
            openReader.Should().Throw<LiteException>().WithMessage("*ownership*");
            ReadShared(file.Filename).Should().Equal(dataBefore);
            ReadShared(logPath).Should().Equal(logBefore);
            writer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "still writable" });
            writer.Dispose();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsString)
                .Should().Equal("committed in WAL", "still writable");
        }

        [Fact]
        public void Readonly_close_preserves_recovered_WAL_without_checkpoint_or_delete()
        {
            using var file = new TempFile();
            using (var writer = new LiteDatabase(file.Filename))
            {
                writer.CheckpointSize = 0;
                writer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            }
            var logPath = Path.ChangeExtension(file.Filename, null) + "-log.db";
            var dataBefore = File.ReadAllBytes(file.Filename);
            var logBefore = File.ReadAllBytes(logPath);
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true }))
            {
                using var reader = new LiteDatabase(engine, disposeOnClose: false);
                Assert.NotNull(reader.GetCollection("rows").FindById(1));
                engine.Close().Should().BeEmpty();
            }
            File.ReadAllBytes(file.Filename).Should().Equal(dataBefore);
            File.ReadAllBytes(logPath).Should().Equal(logBefore);
            File.WriteAllBytes(logPath, Array.Empty<byte>());
            using (var engine = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true }))
                engine.Close().Should().BeEmpty();
            File.Exists(logPath).Should().BeTrue("read-only disposal must not delete even an empty WAL");
        }

        private static byte[] ReadShared(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            input.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
