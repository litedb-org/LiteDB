#if !NETFRAMEWORK
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedSlotGenerations_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-generations-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedSlotGenerations_Tests() => Directory.CreateDirectory(_directory);

        [Theory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public async Task Three_generations_in_one_process_survive_reuse_and_oldest_reader_departure(string password, bool kill)
        {
            await MvccProcess.Run("storage-seed", Filename, password, "Auto,0");
            var cleared = 0;
            using (var snapshots = new MvccProcess("followup-generations", Filename, password))
            using (var writer = new SharedEngine(new EngineSettings
            {
                Filename = Filename, Password = password,
                CheckpointStage = stage => { if (stage == "wal-slot-cleared") cleared++; }
            }))
            {
                for (var generation = 0; generation < 3; generation++)
                {
                    if (generation != 0) await MvccProcess.Run("storage-write", Filename, password, "Auto," + generation);
                    snapshots.Send("open:" + generation);
                    await snapshots.Expect("open:" + generation);
                }
                var registry = new SharedReaderRegistry(Filename);
                registry.LiveVersions().Distinct().Should().HaveCount(3);
                Directory.GetFiles(Filename + "-readers", "*.lease").Should().HaveCount(1);
                for (var revision = 3; revision <= 8; revision++)
                {
                    await MvccProcess.Run("storage-write", Filename, password, "Auto," + revision);
                    writer.Checkpoint();
                    if (revision == 5)
                    {
                        snapshots.Send("verify:0");
                        await snapshots.Expect("verify:0");
                        registry.LiveVersions().Distinct().Should().HaveCount(2);
                    }
                }
                cleared.Should().BeGreaterThan(0, "the test must actually reclaim WAL slots while the snapshots are live");
                snapshots.Send("verify:1");
                await snapshots.Expect("verify:1");
                writer.Checkpoint();
                if (kill) await snapshots.Kill();
                else
                {
                    snapshots.Send("verify:2");
                    await snapshots.Expect("verify:2");
                    snapshots.Send("done");
                    await snapshots.Expect("done");
                    await snapshots.Finish();
                }
                writer.Checkpoint();
                registry.LiveVersions().Should().BeEmpty();
            }
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeFalse();
            // A fresh process checks every payload, the secondary index's plan and results,
            // and the unchanged cold collection after both graceful release and process death.
            await MvccProcess.Run("storage-verify", Filename, password, "Auto,8");
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
#endif
