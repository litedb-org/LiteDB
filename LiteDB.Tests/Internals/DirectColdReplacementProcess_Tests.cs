#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("DirectProcessHost")]
    public class DirectColdReplacementProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-cold-replacement-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public DirectColdReplacementProcess_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData("before-source-backup")]
        [InlineData("after-source-backup")]
        [InlineData("after-temp-install")]
        public async Task Cold_process_opens_cannot_create_or_modify_files_during_replacement(string boundary)
        {
            using (var owner = new LiteDatabase(Filename))
            {
                owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preserved" });
                owner.GetCollection("docs").EnsureIndex("value");
                var observed = 0;
                RebuildService.SimulateInstallFailure = phase =>
                {
                    if (phase != boundary) return;
                    observed++;
                    var before = Directory.GetFiles(_directory).ToDictionary(path => path, File.ReadAllBytes);
                    for (var attempt = 0; attempt < 2; attempt++)
                        MvccProcess.Run("direct-host-rebuild-refused", Filename, null).GetAwaiter().GetResult();
                    Directory.GetFiles(_directory).Should().BeEquivalentTo(before.Keys);
                    foreach (var pair in before) File.ReadAllBytes(pair.Key).Should().Equal(pair.Value);
                };
                try { await Task.Run(() => owner.Rebuild()); }
                finally { RebuildService.SimulateInstallFailure = null; }
                observed.Should().Be(1);
                owner.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("preserved");
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var reopened = new LiteDatabase(Filename);
                reopened.GetCollection("docs").FindOne(Query.EQ("value", "preserved"))["_id"].AsInt32.Should().Be(1);
            }
        }

        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
#endif
