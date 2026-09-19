using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2806EngineFailure_Tests
    {
        private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 13)).ToArray();

        // An engine failure inside a caller transaction rolls that transaction back, so the chunks the
        // failed upload already wrote (staged under negative indexes for a replacement) cannot be committed.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Engine_failure_inside_a_caller_transaction_leaves_no_chunks_to_commit(bool replacesFile)
        {
            using var file = new TempFile();
            using var db = new LiteDatabase(file.Filename);
            var previous = Pattern(300_000);

            if (replacesFile) db.FileStorage.Upload("f", "f.bin", new MemoryStream(previous));
            db.Checkpoint();
            var chunksBefore = db.GetCollection("_chunks").Count();

            db.LimitSize = new FileInfo(file.Filename).Length + 8192 * 40;

            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });

            Action upload = () => db.FileStorage.Upload("f", "f.bin", new MemoryStream(Pattern(5_000_000)));
            upload.Should().Throw<LiteException>();

            // The caller does not know better and commits anyway.
            Record.Exception(() => db.Commit());

            db.GetCollection("_chunks").Count().Should().Be(chunksBefore);
            db.GetCollection("_chunks").Count("_id.n < 0").Should().Be(0);
            db.GetCollection("docs").Count().Should().Be(0, "the failed engine operation rolled the whole transaction back");

            if (replacesFile)
            {
                using var read = new MemoryStream();
                db.FileStorage.Download("f", read);
                read.ToArray().Should().Equal(previous);
            }
            else
            {
                db.FileStorage.Exists("f").Should().BeFalse();
            }

            // The next replacement must not collide with leftovers.
            db.LimitSize = long.MaxValue;
            var next = Pattern(400_000);
            db.FileStorage.Upload("f", "f.bin", new MemoryStream(next));
            using var reread = new MemoryStream();
            db.FileStorage.Download("f", reread);
            reread.ToArray().Should().Equal(next);
        }
    }
}
