#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Internals;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class SharedCoordinationPolicy_Tests
    {
        [MappedFact]
        public void Transient_windows_attachment_failure_retries_before_sticky_fallback()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = new MappedTestFile();
            using var engine = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
            using var database = new LiteDatabase(engine);
            database.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 19 });
            var attempts = 0;
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (!path.EndsWith("-shared-live", StringComparison.Ordinal) || stage != "created") return;
                if (++attempts < 3) throw new IOException("injected sharing violation", unchecked((int)0x80070020));
            };
            try
            {
                for (var i = 0; i < 4; i++) database.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(19);
            }
            finally { SharedCoordinationFile.CreationStage = null; }
            attempts.Should().Be(3);
            engine.CoordinationFallbackReason.Should().BeNull();
            engine.CoordinatedReadHits.Should().BeGreaterThan(0);
            File.Exists(SharedCoordinationFallback.DisabledPath(file)).Should().BeFalse();
        }

        [MappedTheory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task Environment_opt_out_in_a_native_writer_revokes_live_peers(string password)
        {
            using var file = new MappedTestFile();
            await MvccProcess.Run("seed", file, password);
            using (var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) })
            using (var database = new LiteDatabase(engine))
            {
                for (var i = 0; i < 3; i++) database.GetCollection("docs").FindById(0);
                engine.CoordinatedReadHits.Should().BeGreaterThan(0);
                using var child = new MvccProcess("mapped-optout", file, password, disableMappedReads: true);
                await child.Expect("done");
                await child.Finish();
                File.Exists(SharedCoordinationFallback.DisabledPath(file)).Should().BeTrue();
                database.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(7);
            }
            using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            cold.GetCollection("docs").Count().Should().Be(64);
            cold.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(7);
            cold.GetCollection("cold").FindById(63)["payload"].AsString.Should().Be(new string('x', 3000));
        }

        [MappedFact]
        public void Opt_out_preserves_reads_and_revokes_live_mapped_peers_before_writing()
        {
            using var file = new MappedTestFile();
            using var peerEngine = new SharedEngine(new EngineSettings { Filename = file })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
            using var peer = new LiteDatabase(peerEngine);
            peer.GetCollection("rows").InsertBulk(Enumerable.Range(0, 200).Select(id =>
                new BsonDocument { ["_id"] = id, ["value"] = 0, ["payload"] = new string('x', 1000) }));
            for (var i = 0; i < 3; i++) peer.GetCollection("rows").FindById(0);
            peerEngine.CoordinatedReadHits.Should().BeGreaterThan(0);
            AppContext.TryGetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, out var before);
            AppContext.SetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, true);
            try
            {
                using var engine = new SharedEngine(new EngineSettings { Filename = file });
                using var database = new LiteDatabase(engine);
                for (var i = 0; i < 3; i++) database.GetCollection("rows").FindById(0)["value"].AsInt32.Should().Be(0);
                engine.CoordinatedReadHits.Should().Be(0);
                engine.CoordinationFallbackReason.Should().Contain(SharedCoordinationPolicy.DisableMappedSwitch);
                File.Exists(SharedCoordinationFallback.DisabledPath(file)).Should().BeFalse();
                using var held = peer.GetCollection("rows").Query().OrderBy("_id").ToEnumerable().GetEnumerator();
                held.MoveNext().Should().BeTrue();
                database.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 7, ["payload"] = new string('x', 1000) });
                File.Exists(SharedCoordinationFallback.DisabledPath(file)).Should().BeTrue();
                database.Checkpoint();
                while (held.MoveNext()) held.Current["value"].AsInt32.Should().Be(0);
                peer.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(7);
            }
            finally { AppContext.SetSwitch(SharedCoordinationPolicy.DisableMappedSwitch, before); }
            peer.Dispose();
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(200);
            cold.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(7);
        }

        [MappedFact]
        public void Unix_file_locking_switch_refuses_participation_before_creating_files()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
            using var file = new MappedTestFile();
            AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var before);
            AppContext.SetSwitch("System.IO.DisableFileLocking", true);
            try
            {
                Action create = () => new SharedEngine(new EngineSettings { Filename = file }).Dispose();
                var expected = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
                    ? "*file-sharing locks*" : SharedMutexFactory.UnsupportedNativeAotMessage;
                create.Should().Throw<PlatformNotSupportedException>().WithMessage(expected);
                Action map = () => SharedCoordinationPage.Open(file).Dispose();
                map.Should().Throw<PlatformNotSupportedException>();
                File.Exists(SharedCoordinationFallback.LivePath(file)).Should().BeFalse();
            }
            finally { AppContext.SetSwitch("System.IO.DisableFileLocking", before); }
        }

        [MappedTheory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public async Task A_native_process_with_disabled_file_locking_cannot_retire_a_live_authority(string password, bool conflictingSwitch)
        {
            using var file = new MappedTestFile();
            await MvccProcess.Run("seed", file, password);
            using var engine = new SharedEngine(new EngineSettings { Filename = file, Password = password })
                { CoordinatedIdleLimit = TimeSpan.FromMinutes(1) };
            using var database = new LiteDatabase(engine);
            for (var i = 0; i < 3; i++) database.GetCollection("docs").FindById(0);
            var live = File.ReadAllBytes(SharedCoordinationFallback.LivePath(file));
            using (var child = new MvccProcess(conflictingSwitch ? "mapped-locking-disabled-conflict" : "mapped-locking-disabled",
                file, password, disableFileLocking: true))
            {
                await child.Expect("done");
                await child.Finish();
            }
            File.ReadAllBytes(SharedCoordinationFallback.LivePath(file)).Should().Equal(live);
            var hits = engine.CoordinatedReadHits;
            database.GetCollection("docs").FindById(0)["value"].AsInt32.Should().Be(0);
            engine.CoordinatedReadHits.Should().BeGreaterThan(hits);
            database.GetCollection("docs").Count().Should().Be(64);
            database.Dispose();
            using var cold = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            cold.GetCollection("docs").Count().Should().Be(64);
            cold.GetCollection("cold").FindById(63)["payload"].AsString.Should().Be(new string('x', 3000));
        }
    }
}
#endif
