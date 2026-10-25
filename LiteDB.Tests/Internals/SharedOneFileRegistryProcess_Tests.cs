#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    public class SharedOneFileRegistryProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-shm-registry-process-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedOneFileRegistryProcess_Tests() => Directory.CreateDirectory(_directory);

        [OneFileFact]
        public async Task Direct_excludes_new_Shared_in_both_open_orders_and_legacy_family_cannot_attach()
        {
            using (var direct = new LiteDatabase(Filename))
                await MvccProcess.Run("one-file-facade-refused", Filename, null);
            using (var shared = new SharedEngine(new EngineSettings { Filename = Filename, SharedOneFile = true }))
            {
                await MvccProcess.Run("direct-host-direct-refused", Filename, null);
                await MvccProcess.Run("direct-host-shared-refused", Filename, null);
            }
            using (var legacy = new SharedEngine(new EngineSettings { Filename = Filename }))
                await MvccProcess.Run("one-file-facade-refused", Filename, null);
            await MvccProcess.Run("one-file-facade-open", Filename, null);
        }

        [OneFileTheory]
        [InlineData(null)] [InlineData("secret")]
        public async Task Process_kill_releases_participant_and_cold_reopen_keeps_only_acknowledged_indexed_data(string password)
        {
            using (var child = new MvccProcess("one-file-facade-hold", Filename, password))
            {
                await child.Expect("ready");
                await MvccProcess.Run("direct-host-direct-refused", Filename, password);
                await child.Kill();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = Filename, SharedOneFile = true, Password = password });
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("acknowledged");
                Assert.Null(db.GetCollection("docs").FindById(2));
                db.GetCollection("docs").FindOne(Query.EQ("value", "acknowledged"))["_id"].AsInt32.Should().Be(1);
            }
            await MvccProcess.Run("direct-host-direct-open", Filename, password);
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
