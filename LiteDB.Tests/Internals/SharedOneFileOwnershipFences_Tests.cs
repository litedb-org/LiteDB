#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileOwnershipFences_Tests
    {
        [OneFileFact]
        public void Stale_stop_and_off_thread_result_cleanup_cannot_fence_or_release_the_successor()
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            db.BeginTrans().Should().BeTrue();
            var previous = State(Core(engine));
            var tenure = engine.MappedReaderTable.WriterTenure;
            db.Rollback().Should().BeTrue();
            using var old = engine.Query("docs", new Query());
            old.Read().Should().BeTrue();
            db.BeginTrans().Should().BeTrue();
            db.GetCollection("docs").Update(Row(1, 2)).Should().BeTrue();
            var table = engine.MappedReaderTable;
            table.WriterTenure.Should().BeGreaterThan(tenure);
            var owner = table.Authority.Map.Load(SharedMemoryLayout.WriterOwner);
            var sequence = table.Authority.Map.Load(SharedMemoryLayout.Control + 8);
            Task.Run(() =>
            {
                // Model a delayed CompleteStop from an already retired core.
                previous.CompleteStop(new IOException("stale stopped core"), ownsFailure: true);
                old.Dispose();
                Action complete = () => db.Commit();
                complete.Should().Throw<LiteException>().WithMessage("Complete the explicit transaction on the same thread*");
            }).Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            table.Authority.Map.Load(SharedMemoryLayout.WriterOwner).Should().Be(owner);
            table.Authority.Map.Load(SharedMemoryLayout.Control + 8).Should().Be(sequence);
            table.Authority.Map.Load(SharedMemoryLayout.RecoveryOwner).Should().Be(0);
            using (var probe = new SharedRangeLocks(SharedMemoryAuthority.PathFor(files.Filename)))
            {
                Action acquire = () => probe.EnterWrite();
                acquire.Should().Throw<IOException>().WithMessage("Shared main mutex / WRITE ownership mismatch.");
            }
            db.Commit().Should().BeTrue();
            Quiet(table);
            Verify(db, 2);
        }

        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Recovery_barrier_failure_keeps_the_restored_prefix_unknown_until_successful_retry(bool directory)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using (var child = new MvccProcess("one-file-publication-hold", files.Filename, null, "wal-before-durable-flush"))
            {
                await child.Expect("ready");
                await child.Kill();
            }
            var table = engine.MappedReaderTable;
            var before = table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion);
            var hits = 0;
            var published = false;
            SharedMemoryReaderTable.PublicationStage = stage => { if (stage == "open-end") published = true; };
            if (directory) NativeFileSync.SimulateDirectoryErrno = _ => { hits++; return 5; };
            else NativeFileSync.SimulateErrno = path => { if (path == files.Log) { hits++; return 5; } return 0; };
            try
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    Action open = () => _ = db.Timeout;
                    open.Should().Throw<IOException>();
                    hits.Should().BeGreaterThan(repeat);
                    published.Should().BeFalse();
                    table.Authority.Map.Load(SharedMemoryLayout.CommittedVersion).Should().Be(before);
                    Fenced(table);
                    files.WriteIsFree();
                }
            }
            finally
            {
                NativeFileSync.SimulateErrno = null;
                NativeFileSync.SimulateDirectoryErrno = null;
                SharedMemoryReaderTable.PublicationStage = null;
            }
            for (var recovery = 0; recovery < 2; recovery++)
            {
                Verify(db, 1); // complete confirmation survived in OS cache and is now synced
                db.Checkpoint();
                Quiet(table);
            }
        }
    }
}
#endif
