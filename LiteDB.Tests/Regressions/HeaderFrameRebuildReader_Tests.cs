#if DEBUG || TESTING
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Regressions
{
    /// <summary>
    /// Decision 11 in the rebuild's reader: a data header whose first sector never reached the device
    /// is taken from the WAL's header frame, as the engine's open does. The reader read the frame
    /// through a cast to the checksummed WAL stream it had not created yet, so every such header threw
    /// an InvalidCastException, recorded as a reader error that ended its open before any collection
    /// (a rebuild from it would install an empty database). Independent review A, finding 1.
    /// </summary>
    [Trait("Category", "IoSafety")]
    public class HeaderFrameRebuildReader_Tests
    {
        [Fact]
        public void Rebuild_reader_takes_a_lost_header_from_the_header_frame()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase($"Filename={file.Filename};Durable Commits=false"))
            {
                db.CheckpointSize = 0;
                db.GetCollection("rows").Insert(Enumerable.Range(1, 20).Select(Row));
            }
            var data = File.ReadAllBytes(file.Filename);
            Array.Clear(data, 0, 512); // the header's first sector never written back
            File.WriteAllBytes(file.Filename, data);

            var errors = new List<FileReaderError>();
            using var reader = new FileReaderV8(new EngineSettings { Filename = file.Filename }, errors);
            reader.Open();
            errors.Select(e => e.Message).Should().BeEmpty();
            reader.GetCollections().Should().Equal("rows");
            reader.GetDocuments("rows").Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, 20));
        }

        private static BsonDocument Row(int id) => new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 300) };
    }
}
#endif
