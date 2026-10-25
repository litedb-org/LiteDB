#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    public class SharedOneFileSnapshotSafety_Tests
    {
        [OneFileFact]
        public void Mapped_exact_floors_preserve_every_protected_WAL_offset_then_allow_reclamation()
        {
            using var file = new LiteDB.Tests.TempFile();
            var settings = new EngineSettings { Filename = file.Filename, SharedOneFile = true, SharedMode = true };
            var table = SharedMemoryReaderTable.Open(settings);
            using var facade = new SharedMemoryReaders(table);
            using var admission = NativeAdmission.Open(settings, true);
            settings.SharedAdmission = admission;
            settings.SharedReaderVersions = table.LiveVersions;
            using var core = new LiteEngine(settings);
            using var db = new LiteDatabase(core, disposeOnClose: false);
            db.CheckpointSize = 0;
            var leases = new List<IDisposable>();
            var protectedFrames = new Dictionary<long, byte[]>();
            var log = FileHelper.GetLogFile(file.Filename);
            try
            {
                for (var value = 0; value < 3; value++)
                {
                    db.GetCollection("docs").Upsert(Enumerable.Range(0, 64).Select(id =>
                        new BsonDocument { ["_id"] = id, ["value"] = value, ["payload"] = new string((char)('a' + value), 2048) }));
                    var version = core.ReadVersion;
                    leases.Add(facade.Register(version));
                    var bytes = File.ReadAllBytes(log);
                    foreach (var position in core.GetWalIndex().SnapshotPositions(version))
                    {
                        var offset = position / Constants.PAGE_SIZE * WalChecksum.FrameSize;
                        protectedFrames[offset] = bytes.Skip((int)offset).Take(Constants.PAGE_SIZE).ToArray();
                    }
                }
                protectedFrames.Should().NotBeEmpty();
                db.GetCollection("docs").UpdateMany("{ value: 4 }", "true");
                db.Checkpoint();
                var after = File.ReadAllBytes(log);
                foreach (var frame in protectedFrames)
                    after.Skip((int)frame.Key).Take(Constants.PAGE_SIZE).Should().Equal(frame.Value);
                db.GetCollection("docs").FindAll().Should().HaveCount(64).And.OnlyContain(d => d["value"].AsInt32 == 4);
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
            db.Checkpoint();
            core.GetWalIndex().SnapshotCount.Should().Be(0);
            new FileInfo(log).Length.Should().Be(0);
            // TempFile owns only legacy sidecars, so remove the quiescent new file here.
            facade.Dispose();
            File.Delete(file.Filename + "-shm");
        }

        [OneFileFact]
        public void Include_keeps_referenced_collection_at_its_exact_admitted_generation()
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-shm-include-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "test.db");
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = path, SharedOneFile = true });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("refs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "before" });
                db.GetCollection("docs").Insert(Enumerable.Range(0, 256).Select(id => new BsonDocument
                { ["_id"] = id, ["ref"] = new BsonDocument { ["$id"] = 1, ["$ref"] = "refs" }, ["payload"] = new string('x', 2048) }));
                using var reader = engine.Query("docs", new Query { Includes = { "$.ref" } });
                reader.Read().Should().BeTrue();
                db.GetCollection("refs").Update(new BsonDocument { ["_id"] = 1, ["value"] = "after" });
                db.Checkpoint();
                var count = 0;
                do { reader.Current["ref"]["value"].AsString.Should().Be("before"); count++; } while (reader.Read());
                count.Should().Be(256);
                db.GetCollection("refs").FindById(1)["value"].AsString.Should().Be("after");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
#endif
