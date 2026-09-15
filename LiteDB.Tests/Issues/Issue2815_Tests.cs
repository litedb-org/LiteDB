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
            using var readerEngine = new LiteEngine(new EngineSettings { Filename = file.Filename, ReadOnly = true });
            using var reader = new LiteDatabase(readerEngine, disposeOnClose: false);
            reader.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("committed in WAL");
            var errors = readerEngine.Close();
            using (new AssertionScope())
            {
                errors.Should().BeEmpty("swallowing an attempted read-only checkpoint is not a fix");
                ReadShared(file.Filename).Should().Equal(dataBefore);
                ReadShared(logPath).Should().Equal(logBefore);
            }
            writer.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2, ["value"] = "still writable" });
            writer.Dispose();
            using var reopened = new LiteDatabase(file.Filename);
            reopened.GetCollection("rows").FindAll().Select(x => x["value"].AsString)
                .Should().Equal("committed in WAL", "still writable");
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
