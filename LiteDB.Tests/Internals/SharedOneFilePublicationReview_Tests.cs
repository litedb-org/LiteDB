#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFilePublicationReview_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-shm-publication-review-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private EngineSettings Settings => new EngineSettings { Filename = Filename, SharedOneFile = true };

        public SharedOneFilePublicationReview_Tests()
        {
            Directory.CreateDirectory(_directory);
            using var seed = new LiteDatabase(Filename);
            seed.GetCollection("docs").Insert(Row(0));
            seed.GetCollection("docs").EnsureIndex("value");
            seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 42, ["value"] = "untouched" });
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_writable_open_acquires_WRITE_before_recovering_a_partial_data_tail(bool beginTransaction)
        {
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var size = new FileInfo(Filename).Length;
            using (var file = new FileStream(Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                file.SetLength(size + 123);

            // Both routes open a writable core even though the API call itself is a read.
            // A partial last page is a recoverable interrupted append, not a bad header.
            if (beginTransaction)
            {
                db.BeginTrans().Should().BeTrue();
                db.Rollback().Should().BeTrue();
            }
            else db.Timeout.Should().BeGreaterThan(TimeSpan.Zero);
            new FileInfo(Filename).Length.Should().Be(size);
            AssertRows(db, 0);
        }

        [OneFileFact]
        public async Task Live_participant_attach_pairs_WRITE_and_advances_tenure_before_storage_repair()
        {
            using var anchor = new SharedEngine(Settings);
            var map = anchor.MappedReaderTable.Authority.Map;
            var tenure = map.Load(SharedMemoryLayout.WriterTenureEpoch);
            var size = new FileInfo(Filename).Length;
            using var child = new MvccProcess("one-file-review-attach", Filename, null);
            await child.Expect("attached");
            using (var file = new FileStream(Filename, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                file.SetLength(size + 123);
            child.Send("continue");
            var ownership = await child.ReadLine(TimeSpan.FromSeconds(10));
            await child.Expect("done");
            await child.Finish();
            new FileInfo(Filename).Length.Should().Be(size, "the protected constructor actually repaired storage");
            ownership.Should().Be("WRITE-held", "every writable recovery needs main plus native WRITE");
            map.Load(SharedMemoryLayout.WriterTenureEpoch).Should().BeGreaterThan(tenure);
            using var db = new LiteDatabase(anchor, disposeOnClose: false);
            AssertRows(db, 0);
        }

        [OneFileFact]
        public async Task Recovery_syncs_a_dead_writers_confirmation_before_publishing_its_version()
        {
            // Retain DMS so the successor must repair the live authority, rather
            // than resetting all ephemeral state in the first-opener path.
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using (var child = new MvccProcess("one-file-publication-hold", Filename, null, "wal-before-durable-flush"))
            {
                await child.Expect("ready");
                await child.Kill();
            }
            var syncs = 0;
            var syncsAtOpen = -1;
            long recoveredVersion = -1;
            var map = engine.MappedReaderTable.Authority.Map;
            NativeFileSync.SimulateErrno = path =>
            {
                if (path == FileHelper.GetLogFile(Filename)) syncs++;
                return 0;
            };
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != "open-end") return;
                recoveredVersion = map.Load(SharedMemoryLayout.CommittedVersion);
                syncsAtOpen = syncs;
            };
            try
            {
                db.GetCollection("docs").Update(Row(2)).Should().BeTrue();
                recoveredVersion.Should().BeGreaterThan(0, "the successor reconstructed the dead writer's confirmation");
                syncs.Should().BeGreaterThan(0, "the observer detects the later ordinary commit barrier");
                syncsAtOpen.Should().BeGreaterThan(0, "quiet CommittedVersion must not precede its covering durable sync");
                AssertRows(db, 2);
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
                SharedMemoryReaderTable.PublicationStage = null;
            }
        }

        [OneFileFact]
        public void Successful_grouped_commit_publishes_after_its_device_barrier()
        {
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1)).Should().BeTrue();
            var state = Field<EngineState>(Field<LiteEngine>(engine, "_engine"), "_state");
            var synced = false;
            var published = false;
            var publishedBeforeSync = false;
            state.AtCrashPoint = stage => { if (stage == "wal-after-durable-flush") synced = true; };
            SharedMemoryReaderTable.PublicationStage = stage =>
            {
                if (stage != "commit-version") return;
                published = true;
                publishedBeforeSync |= !synced;
            };
            try
            {
                db.Commit().Should().BeTrue();
                synced.Should().BeTrue("this control must exercise group commit");
                published.Should().BeTrue();
                publishedBeforeSync.Should().BeFalse();
                AssertRows(db, 1);
            }
            finally
            {
                state.AtCrashPoint = null;
                SharedMemoryReaderTable.PublicationStage = null;
            }
        }

        [OneFileFact]
        public void Terminal_WAL_failure_must_leave_recovery_fenced_until_an_independent_protected_open()
        {
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1)).Should().BeTrue();
            var core = Field<LiteEngine>(engine, "_engine");
            var state = Field<EngineState>(core, "_state");
            var ended = false;
            var faultReached = false;
            SharedMemoryReaderTable.PublicationStage = stage => { if (stage == "writer-end") ended = true; };
            state.AtCrashPoint = stage =>
            {
                if (stage != "wal-before-durable-flush") return;
                faultReached = true;
                throw new IOException("review: failed WAL device barrier");
            };
            try
            {
                Action commit = () => db.Commit();
                commit.Should().Throw<IOException>();
                faultReached.Should().BeTrue();
                state.Stopped.Should().BeTrue();
                // No successor or recovery has executed between the stop and these loads.
                var map = engine.MappedReaderTable.Authority.Map;
                using var assertions = new FluentAssertions.Execution.AssertionScope();
                ended.Should().BeFalse("a stopped engine cannot complete a successful publication");
                (map.Load(SharedMemoryLayout.Control + 8) & 1).Should().Be(1);
                map.Load(SharedMemoryLayout.RecoveryOwner).Should().NotBe(0);
                SharedMemoryReaderTable.Scan(engine.MappedReaderTable.Authority).Should().BeNull(
                    "reader scans must remain fenced until protected recovery has actually run");
            }
            finally
            {
                state.AtCrashPoint = null;
                SharedMemoryReaderTable.PublicationStage = null;
            }
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Unavailable_or_changed_authority_refuses_writes_before_DB_or_WAL_mutation(bool damageEnvelope)
        {
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var map = engine.MappedReaderTable.Authority.Map;
            var offset = damageEnvelope ? 0 : SharedMemoryLayout.Control;
            var previous = map.Load(offset);
            var data = File.ReadAllBytes(Filename);
            var log = FileHelper.GetLogFile(Filename);
            var wal = File.Exists(log) ? File.ReadAllBytes(log) : Array.Empty<byte>();
            map.Store(offset, 0);
            try
            {
                Action write = () => db.GetCollection("docs").Update(Row(1));
                write.Should().Throw<IOException>("unsupported live authority cannot authorize a writable tenure");
                File.ReadAllBytes(Filename).Should().Equal(data);
                (File.Exists(log) ? File.ReadAllBytes(log) : Array.Empty<byte>()).Should().Equal(wal);
            }
            finally { map.Store(offset, previous); }
            AssertRows(db, 0);
        }

        [OneFileFact]
        public void Writer_epoch_overflow_refuses_without_leaking_the_native_WRITE_byte()
        {
            using var engine = new SharedEngine(Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var table = engine.MappedReaderTable;
            var map = table.Authority.Map;
            var previous = map.Load(SharedMemoryLayout.WriterTenureEpoch);
            map.Store(SharedMemoryLayout.WriterTenureEpoch, long.MaxValue);
            try
            {
                Action write = () => db.GetCollection("docs").Update(Row(1));
                write.Should().Throw<OverflowException>();
                using var probe = new SharedRangeLocks(SharedMemoryAuthority.PathFor(Filename));
                Action acquire = () => probe.EnterWrite();
                acquire.Should().NotThrow("failed acquisition must unwind WRITE before returning the main mutex");
                probe.ExitWrite();
            }
            finally
            {
                map.Store(SharedMemoryLayout.WriterTenureEpoch, previous);
                // Test cleanup only: avoid stranding this process participant after the assertion.
                table.ExitWriter(false);
            }
            AssertRows(db, 0);
        }

        private static T Field<T>(object value, string name) =>
            (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
        private static BsonDocument Row(int value) => new BsonDocument
        { ["_id"] = 1, ["value"] = value, ["payload"] = new string('x', 1024) };
        private static void AssertRows(LiteDatabase db, int value)
        {
            db.GetCollection("docs").FindAll().Should().BeEquivalentTo(new[] { Row(value) });
            db.GetCollection("docs").Find(Query.EQ("value", value)).Should().BeEquivalentTo(new[] { Row(value) });
            db.GetCollection("sentinel").FindById(42)["value"].AsString.Should().Be("untouched");
        }
        public void Dispose() => WindowsFileRelease.DeleteDirectory(_directory);
    }
}
#endif
