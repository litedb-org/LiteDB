using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmission_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Compatible_references_share_one_handle_and_final_release_can_change_threads(bool shared)
        {
            using var file = new TempFile();
            Seed(file);
            var acquisitions = 0;
            var canonical = DatabaseFileIdentity.CanonicalPath(file);
            Action<string, string> observe = (path, stage) =>
            {
                if (path == canonical && stage == "mode-locking") acquisitions++;
            };
            var leases = new SharedModeGuard[32];
            try
            {
                await Task.WhenAll(Enumerable.Range(0, leases.Length).Select(i => Task.Run(() =>
                {
                    SharedCoordinationFile.CreationStage = observe;
                    try { leases[i] = SharedModeGuard.Open(file, shared, SharedMutexNameStrategy.Default); }
                    finally { SharedCoordinationFile.CreationStage = null; }
                })));
                acquisitions.Should().Be(1);
                await Task.WhenAll(leases.Take(31).Select(lease => Task.Run(() => { lease.Dispose(); lease.Dispose(); })));
                Action incompatible = () => { using var guard = SharedModeGuard.Open(file, !shared, SharedMutexNameStrategy.Default); };
                incompatible.Should().Throw<DatabaseAdmissionException>();
                await Task.Run(() => leases[31].Dispose());
                incompatible.Should().NotThrow();
                File.Exists(file.Filename + "-shared-mode").Should().BeFalse();
            }
            finally
            {
                SharedCoordinationFile.CreationStage = null;
                foreach (var lease in leases) lease?.Dispose();
            }
            Verify(file);
        }

        [Fact]
        public void Independent_direct_writers_remain_incompatible_before_mutation()
        {
            using var file = new TempFile();
            Seed(file);
            using (var first = new LiteEngine(file))
            {
                Action second = () => { using var engine = new LiteEngine(file); };
                second.Should().Throw<DatabaseAdmissionException>();
                // Other owners of this process's exclusive admission are compatible.
                using var retained = SharedModeGuard.Open(file, false, SharedMutexNameStrategy.Default);
                first.Insert("rows", new[] { new BsonDocument { ["_id"] = 2, ["value"] = 43 } }, BsonAutoId.Int32);
            }
            using var cold = new LiteDatabase(file);
            cold.GetCollection("rows").Count().Should().Be(2);
        }

        [Fact]
        public void Read_only_direct_objects_share_admission_and_leave_bytes_unchanged()
        {
            using var file = new TempFile();
            Seed(file);
            var data = File.ReadAllBytes(file);
            var readers = Enumerable.Range(0, 16).Select(_ => new LiteDatabase(new ConnectionString
                { Filename = file, ReadOnly = true })).ToArray();
            try
            {
                foreach (var reader in readers) reader.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                Action writer = () => { using var db = new LiteDatabase(file); };
                writer.Should().Throw<DatabaseAdmissionException>();
                using var shared = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared });
                Action write = () => shared.GetCollection("rows").DeleteAll();
                write.Should().Throw<DatabaseAdmissionException>();
            }
            finally { foreach (var reader in readers) reader.Dispose(); }
            File.ReadAllBytes(file).Should().Equal(data);
            Verify(file);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unsupported_locking_fails_before_existing_database_or_WAL_mutation(bool shared)
        {
            using var file = new TempFile();
            Seed(file);
            var data = File.ReadAllBytes(file);
            var log = FileHelper.GetLogFile(file);
            File.Exists(log).Should().BeFalse();
            var canonical = DatabaseFileIdentity.CanonicalPath(file);
            DatabaseFileIdentity.UnsupportedVolume = path => path == canonical;
            try
            {
                Action open = () =>
                {
                    using var db = new LiteDatabase(new ConnectionString
                        { Filename = file, Connection = shared ? ConnectionType.Shared : ConnectionType.Direct });
                    db.GetCollection("rows").DeleteAll();
                };
                open.Should().Throw<DatabaseAdmissionException>().WithMessage("*Unsupported*");
            }
            finally { DatabaseFileIdentity.UnsupportedVolume = null; }
            File.ReadAllBytes(file).Should().Equal(data);
            File.Exists(log).Should().BeFalse();
            Verify(file);
        }

        [Fact]
        public void Forgotten_references_do_not_strand_the_registry()
        {
            using var file = new TempFile();
            Seed(file);
            var weak = Forget(file);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            weak.IsAlive.Should().BeFalse();
            using (SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Default)) { }
            Verify(file);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failure_publishing_a_reference_unwinds_without_releasing_a_live_owner(bool existingOwner)
        {
            using var file = new TempFile();
            Seed(file);
            using var owner = existingOwner ? SharedModeGuard.Open(file, false, SharedMutexNameStrategy.Default) : null;
            SharedCoordinationFile.CreationStage = (path, stage) =>
            {
                if (stage == "mode-retaining") throw new IOException("injected reference publication failure");
            };
            try
            {
                Action open = () => { using var rejected = SharedModeGuard.Open(file, false, SharedMutexNameStrategy.Default); };
                open.Should().Throw<DatabaseAdmissionException>().WithMessage("*reference publication failure*");
            }
            finally { SharedCoordinationFile.CreationStage = null; }
            Action shared = () => { using var lease = SharedModeGuard.Open(file, true, SharedMutexNameStrategy.Default); };
            if (existingOwner) shared.Should().Throw<DatabaseAdmissionException>();
            else shared.Should().NotThrow();
            owner?.Dispose();
            shared.Should().NotThrow();
            Verify(file);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Forget(string file) =>
            new WeakReference(SharedModeGuard.Open(file, false, SharedMutexNameStrategy.Default));

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Forgotten_database_and_engine_release_process_admission(bool shared)
        {
            using var file = new TempFile();
            Seed(file);
            var weak = ForgetDatabase(file, shared);
            // Shared's idle holder and snapshot timers may briefly retain state.
            for (var i = 0; i < 30; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(100);
                if (!weak.IsAlive && i > 2) break;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            weak.IsAlive.Should().BeFalse();
            using (SharedModeGuard.Open(file, !shared, SharedMutexNameStrategy.Default)) { }
            Verify(file);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference ForgetDatabase(string file, bool shared)
        {
            ILiteEngine engine = shared ? (ILiteEngine)new SharedEngine(new EngineSettings { Filename = file }) : new LiteEngine(file);
            var db = new LiteDatabase(engine);
            for (var i = 0; i < 4; i++) db.GetCollection("rows").Count().Should().Be(1);
            return new WeakReference(engine);
        }

        internal static void Seed(string file, string password = null)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
            db.GetCollection("rows").EnsureIndex("value");
            db.GetCollection("untouched").Insert(new BsonDocument { ["_id"] = 1, ["value"] = 99 });
        }

        internal static void Verify(string file, string password = null)
        {
            using var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password });
            db.GetCollection("rows").Find("value = 42").Single()["_id"].AsInt32.Should().Be(1);
            db.GetCollection("untouched").FindById(1)["value"].AsInt32.Should().Be(99);
        }
    }
}
