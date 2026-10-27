#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    [CollectionDefinition("SharedOneFilePublication", DisableParallelization = true)]
    public class SharedOneFilePublicationCollection { }

    [Collection("SharedOneFilePublication")]
    public class SharedOneFilePublication_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-publication-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedOneFilePublication_Tests()
        {
            Directory.CreateDirectory(_directory);
            using var engine = new SharedEngine(new EngineSettings { Filename = Filename, SharedOneFile = true });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var docs = db.GetCollection("docs");
            docs.Insert(new BsonDocument { ["_id"] = 1, ["value"] = 0, ["payload"] = new string('x', 1024) });
            docs.EnsureIndex("value");
        }

        [OneFileFact]
        public async Task Death_at_each_mapped_writer_publication_fence_recovers_complete_storage()
        {
            foreach (var stage in new[] { "writer-begin", "open-begin", "open-end", "commit-version", "writer-end" })
            {
                using (var child = new MvccProcess("one-file-publication-hold", Filename, null, stage))
                {
                    await child.Expect("ready");
                    await child.Kill();
                }

                using (var recovered = new SharedEngine(new EngineSettings { Filename = Filename, SharedOneFile = true }))
                using (var db = new LiteDatabase(recovered, disposeOnClose: false))
                {
                    var value = db.GetCollection("docs").FindById(1)["value"].AsInt32;
                    value.Should().BeOneOf(0, 1);
                    var map = recovered.MappedReaderTable.Authority.Map;
                    (map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(0);
                    map.Load(SharedMemoryLayout.RecoveryOwner).Should().Be(0);
                    map.Load(SharedMemoryLayout.WriterOwner).Should().Be(0);
                    var docs = db.GetCollection("docs");
                    docs.FindAll().Should().ContainSingle();
                    var indexed = docs.Find(Query.EQ("value", value)).Should().ContainSingle().Subject;
                    indexed["payload"].AsString.Should().HaveLength(1024);
                }

                // Reset the acknowledged row so every stage starts from the same
                // complete payload and index state.
                using (var reset = new SharedEngine(new EngineSettings { Filename = Filename, SharedOneFile = true }))
                using (var db = new LiteDatabase(reset, disposeOnClose: false))
                    db.GetCollection("docs").Update(new BsonDocument { ["_id"] = 1, ["value"] = 0, ["payload"] = new string('x', 1024) });
            }
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
