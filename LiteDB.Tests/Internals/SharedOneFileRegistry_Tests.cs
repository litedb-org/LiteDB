#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;
using LiteDB.Tests;

namespace LiteDB.Internals
{
    [CollectionDefinition("SharedOneFileRegistry", DisableParallelization = true)]
    public class SharedOneFileRegistryCollection { }

    [Collection("SharedOneFileRegistry")]
    public class SharedOneFileRegistry_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-registry-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        public SharedOneFileRegistry_Tests() => Directory.CreateDirectory(_directory);
        private EngineSettings Settings(string path = null) => new EngineSettings { Filename = path ?? Filename, SharedOneFile = true };

        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Many_local_facades_join_once_and_dispose_in_either_order(bool reverse)
        {
            var joins = 0;
            SharedMemoryAuthority.BootstrapStage = stage => { if (stage == "claimed") joins++; };
            var engines = new List<SharedEngine>();
            try
            {
                for (var i = 0; i < 20; i++) engines.Add(new SharedEngine(Settings()));
                var table = engines[0].MappedReaderTable;
                joins.Should().Be(1);
                engines.Should().OnlyContain(engine => ReferenceEquals(engine.MappedReaderTable, table));
                using (var db = new LiteDatabase(engines[10], disposeOnClose: false))
                {
                    db.BeginTrans().Should().BeTrue();
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                    db.Commit().Should().BeTrue();
                }
                var order = reverse ? engines.AsEnumerable().Reverse().ToArray() : engines.ToArray();
                foreach (var engine in order.Take(19)) { engine.Dispose(); engine.Dispose(); table.Closed.Should().BeFalse(); }
                using (var survivor = new LiteDatabase(order.Last(), disposeOnClose: false))
                    survivor.GetCollection("docs").FindById(1)["value"].AsString.Should().Be("committed");
                order.Last().Dispose();
                table.Closed.Should().BeTrue();
                using var cold = new SharedEngine(Settings());
                cold.MappedReaderTable.Authority.Header.Authority.Should().NotBe(table.Authority.Header.Authority);
            }
            finally { SharedMemoryAuthority.BootstrapStage = null; foreach (var engine in engines) engine.Dispose(); }
        }

        [OneFileTheory]
        [InlineData("password")] [InlineData("readonly")] [InlineData("durability")]
        [InlineData("collation")] [InlineData("cache")] [InlineData("pages")]
        [InlineData("compact")] [InlineData("transform")] [InlineData("mutex")]
        [InlineData("upgrade")] [InlineData("autorebuild")]
        public void Incompatible_settings_fail_without_affecting_peer(string setting)
        {
            using var first = new SharedEngine(Settings());
            var settings = Settings();
            switch (setting)
            {
                case "password": settings.Password = "wrong"; break;
                case "readonly": settings.ReadOnly = true; break;
                case "durability": settings.DurableCommits = false; break;
                case "collation": settings.Collation = new Collation("en-US/None"); break;
                case "cache": settings.CacheSize = 1024 * 1024; break;
                case "pages": settings.TransactionPageLimit = 1; break;
                case "compact": settings.CompactStorage = CompactStorageMode.Legacy; break;
                case "transform": settings.ReadTransform = (collection, value) => value; break;
                case "mutex": settings.SharedMutexNameStrategy = SharedMutexNameStrategy.Sha1Hash; break;
                case "upgrade": settings.Upgrade = true; break;
                case "autorebuild": settings.AutoRebuild = true; break;
            }
            Action open = () => { using var second = new SharedEngine(settings); };
            open.Should().Throw<InvalidOperationException>().WithMessage("*Incompatible Shared engine settings*");
            using var compatible = new SharedEngine(Settings());
            compatible.MappedReaderTable.Should().BeSameAs(first.MappedReaderTable);
        }

        [OneFileFact]
        public async Task Escaping_snapshot_keeps_mapping_and_admission_after_last_facade_disposes()
        {
            using (var seed = new LiteDatabase(Filename)) seed.GetCollection("docs").Insert(Enumerable.Range(0, 256)
                .Select(id => new BsonDocument { ["_id"] = id, ["payload"] = new string('x', 2048) }));
            var engine = new SharedEngine(Settings());
            var table = engine.MappedReaderTable;
            using (var result = engine.Query("docs", new Query()))
            {
                result.Read().Should().BeTrue();
                engine.Dispose();
                table.Closed.Should().BeFalse();
                using (var attached = new SharedEngine(Settings())) attached.MappedReaderTable.Should().BeSameAs(table);
                await MvccProcess.Run("direct-host-direct-refused", Filename, null);
                var count = 1;
                while (result.Read()) { result.Current["payload"].AsString.Should().Be(new string('x', 2048)); count++; }
                count.Should().Be(256);
            }
            table.Closed.Should().BeTrue();
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }

        [OneFileFact]
        public async Task Nonfinal_disposal_preserves_peers_transaction_and_finalization_releases_abandoned_facade()
        {
            using var first = new SharedEngine(Settings());
            using var second = new SharedEngine(Settings());
            using var owner = new LiteDatabase(second, disposeOnClose: false);
            owner.BeginTrans();
            owner.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            first.Dispose();
            owner.Commit().Should().BeTrue();
            var abandoned = Abandon(Settings());
            for (var pass = 0; pass < 3; pass++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            abandoned.IsAlive.Should().BeFalse();
            var table = second.MappedReaderTable;
            second.Dispose();
            table.Closed.Should().BeTrue();
            await MvccProcess.Run("direct-host-direct-open", Filename, null);
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference Abandon(EngineSettings settings) => new WeakReference(new SharedEngine(settings));

        [OneFileFact]
        public async Task Slow_initialization_does_not_block_an_unrelated_database_open()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var slow = Task.Run(() =>
            {
                SharedMemoryAuthority.BootstrapStage = stage =>
                { if (stage == "before-storage") { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(); } };
                try { return new SharedEngine(Settings()); }
                finally { SharedMemoryAuthority.BootstrapStage = null; }
            });
            try
            {
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                var other = Task.Run(() => new SharedEngine(Settings(Path.Combine(_directory, "other.db"))));
                (await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(other);
                using var unrelated = await other;
            }
            finally { release.Set(); using var initialized = await slow; }
        }

        [OneFileFact]
        public async Task Concurrent_compatible_opens_use_one_participant_and_preserve_all_commits()
        {
            using var anchor = new SharedEngine(Settings());
            var table = anchor.MappedReaderTable;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var pass = 0; pass < 8; pass++)
                {
                    using var engine = new SharedEngine(Settings());
                    engine.MappedReaderTable.Should().BeSameAs(table);
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = worker * 8 + pass });
                }
            })));
            using var check = new LiteDatabase(anchor, disposeOnClose: false);
            check.GetCollection("docs").FindAll().Select(d => d["_id"].AsInt32).Should().BeEquivalentTo(Enumerable.Range(0, 64));
        }

        [OneFileFact]
        public async Task Open_racing_last_mapping_close_waits_then_initializes_a_new_generation()
        {
            var engine = new SharedEngine(Settings());
            var old = engine.MappedReaderTable.Authority.Header.Authority;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var closing = Task.Run(() =>
            {
                SharedMemoryAuthority.BootstrapStage = stage =>
                { if (stage == "before-close") { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(); } };
                try { engine.Dispose(); }
                finally { SharedMemoryAuthority.BootstrapStage = null; }
            });
            try
            {
                entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                var opening = Task.Run(() => new SharedEngine(Settings()));
                await Task.Delay(50);
                opening.IsCompleted.Should().BeFalse();
                release.Set();
                await closing;
                using var fresh = await opening;
                fresh.MappedReaderTable.Authority.Header.Authority.Should().NotBe(old);
            }
            finally { release.Set(); await closing; }
        }

        [OneFileFact]
        public void Construction_time_cwd_symlink_case_and_separate_wal_namespace_are_preserved()
        {
            var cwd = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = _directory;
                using var first = new SharedEngine(Settings("test.db"));
                Environment.CurrentDirectory = Path.GetTempPath();
                var alias = Path.Combine(_directory, "alias.db");
                File.CreateSymbolicLink(alias, Filename);
                using var second = new SharedEngine(Settings(alias));
                second.MappedReaderTable.Should().BeSameAs(first.MappedReaderTable);
                using var distinct = new SharedEngine(Settings(Path.Combine(_directory, "Test.db")));
                distinct.MappedReaderTable.Should().NotBeSameAs(first.MappedReaderTable);
                File.WriteAllBytes(FileHelper.GetLogFile(alias), new byte[] { 1, 2, 3 });
                Action badAlias = () => { using var bad = new SharedEngine(Settings(alias)); };
                badAlias.Should().Throw<IOException>().WithMessage("*separate WAL*");
                Directory.GetFiles(_directory, "alias.db-shm").Should().BeEmpty();
            }
            finally { Environment.CurrentDirectory = cwd; }
        }

        [OneFileFact]
        public void Default_legacy_file_locking_refusal_creates_no_database_or_coordination_files()
        {
            AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var previous);
            AppContext.SetSwitch("System.IO.DisableFileLocking", true);
            try
            {
                Action open = () => { using var engine = new SharedEngine(new EngineSettings { Filename = Filename }); };
                open.Should().Throw<PlatformNotSupportedException>();
                Directory.GetFileSystemEntries(_directory).Should().BeEmpty();
            }
            finally { AppContext.SetSwitch("System.IO.DisableFileLocking", previous); }
        }

        [OneFileFact]
        public void Hard_links_refuse_without_mutating_storage_or_creating_an_authority()
        {
            using (var seed = new LiteDatabase(Filename)) seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            var alias = Path.Combine(_directory, "linked.db");
            link(Filename, alias).Should().Be(0);
            var before = File.ReadAllBytes(Filename);
            Action open = () => { using var engine = new SharedEngine(Settings(alias)); };
            open.Should().Throw<IOException>().WithMessage("*Hard-linked*");
            File.ReadAllBytes(Filename).Should().Equal(before);
            File.Exists(alias + "-shm").Should().BeFalse();
            File.Delete(alias);
        }

        [OneFileFact]
        public async Task Failed_password_initialization_releases_native_resources_before_retry()
        {
            using (var seed = new LiteDatabase(new ConnectionString { Filename = Filename, Password = "secret" }))
                seed.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
            Action wrong = () => { using var engine = new SharedEngine(Settings()); };
            wrong.Should().Throw<LiteException>();
            var settings = Settings(); settings.Password = "secret";
            using (var valid = new SharedEngine(settings)) valid.MappedReaderTable.Should().NotBeNull();
            await MvccProcess.Run("direct-host-direct-open", Filename, "secret");
        }
        [DllImport("libc", SetLastError = true)] private static extern int link(string original, string alias);
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
#endif
