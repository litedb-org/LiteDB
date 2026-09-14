using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2825_Tests
    {
        [Fact]
        public void Parallel_collection_page_reuse_preserves_every_surviving_payload_after_reopen()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(file.Filename))
            {
                // Create collections first to isolate this free-list bug from #2792.
                for (var worker = 0; worker < 4; worker++) db.GetCollection("rows" + worker).EnsureIndex("value");
                Parallel.For(0, 4, worker =>
                {
                    var col = db.GetCollection("rows" + worker);
                    for (var round = 0; round < 30; round++)
                    {
                        col.DeleteAll().Should().Be(round == 0 ? 0 : 12);
                        var payload = new string((char)('A' + worker), 10000 + round * 11);
                        col.Insert(Enumerable.Range(1, 12).Select(id => new BsonDocument
                        {
                            ["_id"] = id, ["value"] = round, ["payload"] = payload
                        }));
                        col.FindAll().Should().HaveCount(12).And.OnlyContain(x => x["payload"].AsString == payload);
                    }
                });
            }
            using var reopened = new LiteDatabase(file.Filename);
            for (var worker = 0; worker < 4; worker++)
            {
                var col = reopened.GetCollection("rows" + worker);
                col.FindAll().Select(x => x["_id"].AsInt32).OrderBy(x => x).Should().Equal(Enumerable.Range(1, 12));
                var payload = new string((char)('A' + worker), 10000 + 29 * 11);
                col.FindAll().Should().OnlyContain(x => x["value"].AsInt32 == 29 && x["payload"].AsString == payload);
                col.Insert(new BsonDocument { ["_id"] = 13, ["payload"] = new string('Z', 20000) });
                col.FindById(13)["payload"].AsString.Should().Be(new string('Z', 20000));
            }
        }

        [Fact]
        public void Parallel_filestorage_upload_delete_leaves_no_orphan_chunks()
        {
            using var db = new LiteDatabase(":memory:");
            using (var seed = new MemoryStream(new byte[] { 1 })) db.FileStorage.Upload("seed", "seed", seed);
            db.FileStorage.Delete("seed").Should().BeTrue();
            Parallel.For(0, 8, worker =>
            {
                for (var round = 0; round < 12; round++)
                {
                    var id = worker + "/" + round;
                    var bytes = Enumerable.Repeat((byte)(worker + round + 1), 70000).ToArray();
                    using var source = new MemoryStream(bytes);
                    db.FileStorage.Upload(id, id, source);
                    using var actual = new MemoryStream();
                    db.FileStorage.Download(id, actual);
                    actual.ToArray().Should().Equal(bytes);
                    db.FileStorage.Delete(id).Should().BeTrue();
                }
            });
            db.GetCollection("_files").Count().Should().Be(0);
            db.GetCollection("_chunks").Count().Should().Be(0);
        }
    }
}
