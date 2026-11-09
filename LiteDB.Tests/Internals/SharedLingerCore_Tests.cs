#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFileRegistry")]
    public sealed class SharedLingerCore_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-linger-core-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedLingerCore_Tests() => Directory.CreateDirectory(_directory);
        private static SharedEngine Engine(LiteDatabase db) => (SharedEngine)typeof(LiteDatabase)
            .GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(db);
        private ConnectionString Connection => new ConnectionString { Filename = Filename, Connection = ConnectionType.Shared, SharedOneFile = true };

        [OneFileFact]
        public void Public_configuration_roundtrips_and_validates_duration()
        {
            new EngineSettings().SharedLinger.Should().Be(1000);
            new ConnectionString().SharedLinger.Should().Be(1000);
            var parsed = new ConnectionString("filename=test.db;connection=shared;shared linger=0");
            parsed.SharedLinger.Should().Be(0);
            new ConnectionString(parsed.ToString()).SharedLinger.Should().Be(0);
            Action badSetting = () => new EngineSettings { SharedLinger = -1 };
            badSetting.Should().Throw<ArgumentOutOfRangeException>();
            Action badConnection = () => new ConnectionString("shared linger=-1");
            badConnection.Should().Throw<ArgumentOutOfRangeException>();
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Public_reopen_reuses_engine_mapping_and_native_admission(bool readOnly)
        {
            using (var seed = new LiteDatabase(Filename))
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
            var connection = Connection;
            connection.ReadOnly = readOnly;
            SharedMemoryReaderTable table;
            LiteEngine core;
            object admission;
            var bootstraps = 0;
            SharedMemoryAuthority.BootstrapStage = stage => { if (stage == "claimed") bootstraps++; };
            try
            {
                using (var first = new LiteDatabase(connection))
                {
                    first.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("saved");
                    table = Engine(first).MappedReaderTable;
                    core = readOnly ? SharedOneFilePublicationFixture.Field<LiteEngine>(table.ProtectedSnapshot, "_engine") : table.ReadCache.Snapshot.Engine;
                    core.Should().NotBeNull();
                    admission = table.Admission.RegistryKey;
                }
                Thread.Sleep(200); // Beyond the previous 100 ms snapshot expiry.
                using (var second = new LiteDatabase(connection))
                {
                    var shared = Engine(second);
                    shared.MappedReaderTable.Should().BeSameAs(table);
                    shared.MappedReaderTable.Admission.RegistryKey.Should().BeSameAs(admission);
                    second.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("saved");
                    var reused = readOnly ? SharedOneFilePublicationFixture.Field<LiteEngine>(table.ProtectedSnapshot, "_engine") : table.ReadCache.Snapshot.Engine;
                    reused.Should().BeSameAs(core);
                    shared.SnapshotOpens.Should().Be(0);
                    if (!readOnly) shared.CoordinatedReadHits.Should().Be(1);
                    bootstraps.Should().Be(1);
                }
                LiteDatabase.ReleaseIdle(Filename);
                table.Closed.Should().BeTrue();
            }
            finally { SharedMemoryAuthority.BootstrapStage = null; }
        }

        [OneFileFact]
        public void Default_retains_wal_until_expiry_then_runs_final_checkpoint()
        {
            SharedMemoryReaderTable table;
            using (var db = new LiteDatabase(Connection))
            {
                table = Engine(db).MappedReaderTable;
                db.CheckpointSize = 1000;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
                db.GetCollection("docs").EnsureIndex("value");
            }
            table.Closed.Should().BeFalse();
            File.Exists(FileHelper.GetLogFile(Filename)).Should().BeTrue();
            SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
            using var reopened = new LiteDatabase(Filename);
            reopened.GetCollection("docs").FindOne(Query.EQ("value", "saved"))["_id"].AsInt32.Should().Be(1);
            reopened.GetCollection("docs").Count().Should().Be(1);
        }

        [OneFileFact]
        public void Failed_expiry_checkpoint_preserves_wal_for_repeated_recovery()
        {
            var connection = Connection;
            connection.SharedLinger = 200;
            SharedMemoryReaderTable table;
            var reached = false;
            using (var db = new LiteDatabase(connection))
            {
                table = Engine(db).MappedReaderTable;
                db.CheckpointSize = 1000;
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
                db.GetCollection("docs").EnsureIndex("value");
                db.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42, ["value"] = "unrelated" });
                EngineState.SimulateProcessCrash = stage =>
                {
                    if (stage == "checkpoint-after-page-write")
                    {
                        reached = true;
                        throw new IOException("injected expiry checkpoint failure");
                    }
                };

            }
            try
            {
                SpinWait.SpinUntil(() => table.Closed, TimeSpan.FromSeconds(5)).Should().BeTrue();
                reached.Should().BeTrue();
                File.Exists(FileHelper.GetLogFile(Filename)).Should().BeTrue();
            }
            finally { EngineState.SimulateProcessCrash = null; }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(Filename);
                db.GetCollection("docs").FindOne(Query.EQ("value", "saved"))["_id"].AsInt32.Should().Be(1);
                db.GetCollection("sentinel").FindById(42)["value"].AsString.Should().Be("unrelated");
                db.GetCollection("docs").Count().Should().Be(1);
            }
        }

        [OneFileFact]
        public void Reused_participant_preserves_same_thread_transaction_refusal_and_rollback()
        {
            using (var warm = new LiteDatabase(Connection)) warm.GetCollection("docs").Count().Should().Be(0);
            using var first = new LiteDatabase(Connection);
            using var second = new LiteDatabase(Connection);
            first.BeginTrans().Should().BeTrue();
            first.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            LiteDatabase.ReleaseIdle(Filename);
            LiteDatabase.ReleaseAllIdle();
            Action peerBegin = () => second.BeginTrans();
            peerBegin.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(141);
            first.Rollback().Should().BeTrue();
            second.GetCollection("docs").Count().Should().Be(0);
            second.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 2 });
            Assert.NotNull(first.GetCollection("docs").FindById(2));
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Lingering_process_death_or_exit_recovers_indexed_acknowledged_data(bool crash)
        {
            using (var child = new MvccProcess("one-file-linger-idle", Filename, null))
            {
                await child.Expect("idle");
                if (crash) await child.Kill();
                else { child.Send("exit"); await child.Expect("done"); await child.Finish(); }
            }
            File.Exists(Filename + ".exit-checkpoint").Should().Be(!crash);
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(Filename);
                db.GetCollection("docs").FindOne(Query.EQ("value", "acknowledged"))["_id"].AsInt32.Should().Be(1);
                db.GetCollection("docs").Count().Should().Be(1);
            }
        }

        [OneFileFact]
        public async Task Read_only_cached_engine_is_discarded_after_a_remote_writer()
        {
            using (var seed = new LiteDatabase(Filename))
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
            var connection = Connection;
            connection.ReadOnly = true;
            using var db = new LiteDatabase(connection);
            db.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("saved");
            var table = Engine(db).MappedReaderTable;
            var before = SharedOneFilePublicationFixture.Field<LiteEngine>(table.ProtectedSnapshot, "_engine");
            before.Should().NotBeNull();
            await MvccProcess.Run("one-file-linger-change", Filename, null);
            db.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("changed");
            Engine(db).SnapshotOpens.Should().Be(2);
        }

        [OneFileTheory]
        [InlineData("open-begin", false)]
        [InlineData("commit-version", true)]
        public async Task Remote_writer_death_while_idle_forces_independent_recovery_on_open(string cut, bool committed)
        {
            var connection = Connection;
            connection.SharedLinger = 60000;
            using (var seed = new LiteDatabase(connection))
            {
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
                seed.GetCollection("docs").EnsureIndex("value");
                seed.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("saved");
            }
            using (var child = new MvccProcess("one-file-publication-hold", Filename, null, cut))
            {
                await child.Expect("ready");
                await child.Kill(sharedFilesRemainOpen: true);
            }
            using (var recovered = new LiteDatabase(connection))
            {
                var map = Engine(recovered).MappedReaderTable.Authority.Map;
                (map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(0);
                map.Load(SharedMemoryLayout.RecoveryOwner).Should().Be(0);
                map.Load(SharedMemoryLayout.WriterOwner).Should().Be(0);
                var expected = committed ? new BsonValue(1) : new BsonValue("saved");
                recovered.GetCollection("docs").FindOne(Query.EQ("value", expected))["_id"].AsInt32.Should().Be(1);
            }
            LiteDatabase.ReleaseIdle(Filename);
            using var cold = new LiteDatabase(Filename);
            var value = cold.GetCollection("docs").FindById(1)["value"];
            Assert.Equal(committed ? new BsonValue(1) : new BsonValue("saved"), value);
        }

        [OneFileFact]
        public async Task Rebuild_releases_protected_cached_storage_before_replacement()
        {
            using (var seed = new LiteDatabase(Filename))
            {
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
                seed.GetCollection("docs").EnsureIndex("value");
            }
            using (var child = new MvccProcess("one-file-linger-rebuild", Filename, null, disableMappedReads: true))
            {
                await child.Expect("done");
                await child.Finish();
            }
            for (var repeat = 0; repeat < 2; repeat++)
            {
                using var db = new LiteDatabase(Filename);
                db.GetCollection("docs").FindOne(Query.EQ("value", "saved"))["_id"].AsInt32.Should().Be(1);
                db.GetCollection("docs").Count().Should().Be(1);
            }
        }

        [OneFileFact]
        public void Assembly_unloading_releases_idle_admission_before_its_timer()
        {
            var context = new System.Runtime.Loader.AssemblyLoadContext("linger-" + Guid.NewGuid(), isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(typeof(LiteDatabase).Assembly.Location);
                var database = assembly.GetType("LiteDB.LiteDatabase");
                var instance = database.GetConstructor(new[] { typeof(string), assembly.GetType("LiteDB.BsonMapper") })
                    .Invoke(new object[] { "filename=" + Filename + ";connection=shared;shared one file=true;shared linger=60000", null });
                ((IDisposable)instance).Dispose();
                Action directWhileIdle = () => { using var denied = new LiteDatabase(Filename); };
                directWhileIdle.Should().Throw<IOException>();
            }
            finally { context.Unload(); }
            using var released = new LiteDatabase(Filename);
            released.GetCollection("docs").Count().Should().Be(0);
        }

        [OneFileFact]
        public void Legacy_shared_ignores_linger_and_zero_releases_for_immediate_direct_open()
        {
            var connection = Connection;
            connection.SharedOneFile = false;
            using (var legacy = new LiteDatabase(connection)) legacy.GetCollection("docs").Count().Should().Be(0);
            using (var direct = new LiteDatabase(Filename)) direct.GetCollection("docs").Count().Should().Be(0);
            connection.SharedOneFile = true;
            connection.SharedLinger = 0;
            SharedMemoryReaderTable table;
            using (var noLinger = new LiteDatabase(connection)) table = Engine(noLinger).MappedReaderTable;
            table.Closed.Should().BeTrue();
            using var reopened = new LiteDatabase(Filename);
            reopened.GetCollection("docs").Count().Should().Be(0);
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Idle_release_preserves_deleted_and_replaced_paths(bool replace)
        {
            var connection = Connection;
            connection.SharedLinger = 60000;
            using (var db = new LiteDatabase(connection))
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "saved" });
            var replacement = Path.Combine(_directory, "replacement.db");
            byte[] expected = null;
            if (replace)
            {
                using (var other = new LiteDatabase(replacement))
                    other.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42, ["value"] = "unrelated" });
                expected = File.ReadAllBytes(replacement);
            }
            try { File.Delete(Filename); }
            catch (IOException) when (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            {
                LiteDatabase.ReleaseIdle(Filename);
                File.Delete(Filename);
            }
            if (replace)
            {
                try { File.Copy(replacement, Filename); }
                catch (IOException) when (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    LiteDatabase.ReleaseIdle(Filename);
                    File.Copy(replacement, Filename);
                }
            }
            LiteDatabase.ReleaseIdle(Filename);
            if (replace) File.ReadAllBytes(Filename).Should().Equal(expected);
            else File.Exists(Filename).Should().BeFalse();
        }

        public void Dispose()
        {
            LiteDatabase.ReleaseIdle(Filename);
            WindowsFileRelease.DeleteDirectory(_directory);
        }
    }
}
#endif
