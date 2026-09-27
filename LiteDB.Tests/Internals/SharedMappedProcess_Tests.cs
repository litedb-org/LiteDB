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
    public class SharedMappedProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-mapped-process-" + Guid.NewGuid().ToString("N"));
        private bool _passed;
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedMappedProcess_Tests() => Directory.CreateDirectory(_directory);

        public static System.Collections.Generic.IEnumerable<object[]> CreationBoundaries()
        {
            foreach (var suffix in new[] { "-shared-live", "-shared-state", "-shared-disabled" })
                foreach (var stage in new[] { "created", "written", "flushed", "published" })
                    foreach (var password in new[] { null, "secret" })
                        yield return new object[] { suffix, stage, password };
        }

        [MappedTheory]
        [MemberData(nameof(CreationBoundaries))]
        public async Task Death_during_control_creation_preserves_data_and_recovers_mapped_reads(string suffix, string stage, string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            SharedCoordinationPage.TryRetire(Filename);
            using (var child = new MvccProcess("mapped-control-create", Filename, password, suffix + ":" + stage))
            {
                await child.Expect("ready");
                File.Exists(Filename + suffix).Should().Be(stage == "published");
                await child.Kill();
            }
            // A surviving temporary file must never prevent a fresh authority.
            using (var engine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password }))
            using (var database = new LiteDatabase(engine))
            {
                for (var i = 0; i < 3; i++) database.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(0);
                var authority = (SharedCoordinationPage)typeof(SharedEngine).GetField("_coordination",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(engine);
                authority.Should().NotBeNull("a crash must not permanently disable the mapped path");
                authority.TryRead(out _).Should().BeTrue();
            }
            Directory.GetFiles(_directory, ".ldb-*-*").Should().BeEmpty("recognized crashed publications are cleaned under ownership");
            await MvccProcess.Run("write", Filename, password, "7");
            await MvccProcess.Run("checkpoint", Filename, password);
            VerifyCold(password, 7);
        }

        [MappedTheory]
        [InlineData(null, "cached-status", false)]
        [InlineData(null, "lease-published", false)]
        [InlineData("secret", "cached-status", false)]
        [InlineData("secret", "lease-published", false)]
        [InlineData(null, "cached-status", true)]
        [InlineData(null, "lease-published", true)]
        public async Task Checkpoint_and_reader_death_at_native_admission_boundaries(string password, string stage, bool kill)
        {
            await MvccProcess.Run("seed", Filename, password);
            using (var child = new MvccProcess("mapped-admission", Filename, password, stage))
            {
                await child.Expect("ready");
                await MvccProcess.Run("write", Filename, password, "7");
                await MvccProcess.Run("checkpoint", Filename, password);
                if (kill) await child.Kill();
                else
                {
                    child.Send("continue");
                    await child.Expect("done");
                    await child.Finish();
                }
            }
            await MvccProcess.Run("checkpoint", Filename, password);
            VerifyCold(password, 7);
        }

        [MappedTheory]
        [InlineData("mapped-slot-half", null)]
        [InlineData("mapped-opening", null)]
        [InlineData("mapped-recovering", null)]
        [InlineData("mapped-recovering", "secret")]
        public async Task Death_during_partial_lease_or_writer_open_recovers_without_lost_data(string mode, string password)
        {
            await MvccProcess.Run("seed", Filename, password);
            using var observerEngine = new SharedEngine(new EngineSettings { Filename = Filename, Password = password });
            using var observer = new LiteDatabase(observerEngine);
            for (var i = 0; i < 3; i++) observer.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(0);
            var authority = (SharedCoordinationPage)typeof(SharedEngine).GetField("_coordination",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(observerEngine);
            using (var child = new MvccProcess(mode, Filename, password))
            {
                await child.Expect("ready");
                if (mode == "mapped-slot-half")
                {
                    using (var registry = new SharedReaderRegistry(Filename))
                        registry.LiveVersions().Should().BeNull("a half-written live slot must fail closed");
                }
                if (mode == "mapped-opening") authority.TryRead(out _).Should().BeTrue("a clean open has not invalidated storage");
                if (mode == "mapped-recovering") authority.TryRead(out _).Should().BeFalse("tail repair must exclude new cached admissions");
                // Preserve the pre-recovery durable files before opening any successor.
                foreach (var path in Directory.GetFiles(_directory, "*.db"))
                    File.Copy(path, path + ".before-recovery");
                await child.Kill();
            }
            await MvccProcess.Run("write", Filename, password, "7");
            await MvccProcess.Run("checkpoint", Filename, password);
            authority.TryRead(out _).Should().BeTrue("the surviving mapping must observe recovered publication");
            observer.GetCollection("docs").FindById(63)["value"].AsInt32.Should().Be(7);
            observer.Dispose();
            VerifyCold(password, 7);
        }

        private void VerifyCold(string password, int revision)
        {
            using (var database = new LiteDatabase(new ConnectionString { Filename = Filename, Password = password }))
            {
                foreach (var name in new[] { "docs", "cold" })
                {
                    var rows = database.GetCollection(name).FindAll().OrderBy(row => row["_id"].AsInt32).ToArray();
                    rows.Length.Should().Be(64);
                    for (var id = 0; id < rows.Length; id++)
                    {
                        rows[id]["_id"].AsInt32.Should().Be(id);
                        rows[id]["value"].AsInt32.Should().Be(name == "docs" ? revision : 0);
                        rows[id]["payload"].AsString.Should().Be(new string('x', 3000));
                        database.GetCollection(name).FindById(id).ToString().Should().Be(rows[id].ToString());
                    }
                }
            }
            _passed = true;
        }

        public void Dispose()
        {
            if (_passed) Directory.Delete(_directory, true);
            else Console.Error.WriteLine("Preserved failed mapped-process database: " + _directory);
        }
    }
}
#endif
