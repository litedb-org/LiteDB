#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedOneFileDefaultProcess_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public async Task Default_and_legacy_refuse_mixed_processes_in_both_orders(bool legacyFirst)
        {
            var directory = Path.Combine(SharedMappedDirectory.Root, "default-mixed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "test.db");
            try
            {
                using (var seed = new LiteDatabase(filename))
                    seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "accepted" });
                using (var peer = new MvccProcess(legacyFirst ? "default-family-legacy-hold" : "default-family-mapped-hold", filename, null))
                {
                    await peer.Expect("ready");
                    var data = File.ReadAllBytes(filename);
                    await MvccProcess.Run(legacyFirst ? "default-family-mapped-refused" : "default-family-legacy-refused", filename, null);
                    File.ReadAllBytes(filename).Should().Equal(data);
                    await peer.Finish(release: true);
                }
                using var engine = new SharedEngine(new EngineSettings { Filename = filename });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("docs").FindById(1)["value"].Should().Be("accepted");
            }
            finally { WindowsFileRelease.DeleteDirectory(directory); }
        }
    }
}
#endif
