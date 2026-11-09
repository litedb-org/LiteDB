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
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileRetryHandoff_Tests
    {
        [OneFileTheory]
        [InlineData(false)] [InlineData(true)]
        public void Real_abandoned_thread_with_live_handles_requires_actual_WRITE_and_can_retry_after_timeout(bool timeout)
        {
            using var file = new MappedTestFile();
            using var main = SharedMutexFactory.Create("litedb-retry-" + Guid.NewGuid().ToString("N"));
            using var stopped = new SharedRangeLocks(file.Filename + "-shm");
            var dead = new Thread(() => { main.WaitOne(); stopped.EnterWrite(); });
            dead.Start(); dead.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var abandoned = false;
            try { main.WaitOne(); } catch (AbandonedMutexException) { abandoned = true; }
            abandoned.Should().BeTrue("this is actual thread death inside a still-live process");
            using var successor = new SharedRangeLocks(file.Filename + "-shm");
            var hits = 0;
            SharedRangeLocks.WriteRetry = () =>
            {
                hits++;
                if (!timeout) stopped.ExitWrite();
            };
            try
            {
                Action acquire = () => successor.EnterWrite(abandoned);
                if (timeout)
                {
                    acquire.Should().Throw<IOException>().WithMessage("*ownership mismatch*");
                    // A failed acquisition never owns WRITE and cannot release the live holder.
                    successor.ExitWrite();
                    Action probe = () => successor.EnterWrite();
                    probe.Should().Throw<IOException>();
                    stopped.ExitWrite();
                    successor.EnterWrite(abandoned);
                }
                else acquire.Should().NotThrow();
                hits.Should().BeGreaterThan(0);
                successor.ExitWrite();
                stopped.EnterWrite(); stopped.ExitWrite();
            }
            finally { SharedRangeLocks.WriteRetry = null; main.ReleaseMutex(); }
        }

        [OneFileTheory]
        [InlineData("constructor")]
        [InlineData("ordinary")]
        [InlineData("fallback")]
        [InlineData("pinned")]
        [InlineData("final-close")]
        public async Task Every_protected_entry_propagates_abandonment_until_the_real_WRITE_byte_is_acquired(string route)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var remote = new MvccProcess("one-file-dimensions-hold", files.Filename, null, "reader");
            await remote.Expect("ready");
            SharedEngine engine = route == "constructor" ? null : new SharedEngine(files.Settings);
            IBsonDataReader anchor = null;
            if (route == "pinned")
            {
                anchor = engine.Query("docs", new Query());
                anchor.Read().Should().BeTrue();
            }
            var snapshotFailures = 0;
            if (route == "fallback") engine.OwnershipStage = stage =>
            {
                if (stage == "before-snapshot-open")
                { snapshotFailures++; throw new IOException("snapshot open failure"); }
            };
            if (route == "final-close")
            {
                using var db = new LiteDatabase(engine, disposeOnClose: false);
                db.CheckpointSize = 0;
                db.GetCollection("docs").Update(Row(1, 1));
            }
            using var blocked = new SharedRangeLocks(files.Filename + "-shm");
            var settings = files.Settings;
            using var mutex = SharedMutexFactory.Create(SharedMutexNameFactory.Create(files.Filename, settings.SharedMutexNameStrategy));
            var dead = new Thread(() => { mutex.WaitOne(); blocked.EnterWrite(); });
            dead.Start(); dead.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var hits = 0;
            SharedRangeLocks.WriteRetry = () => { hits++; blocked.ExitWrite(); };
            try
            {
                if (route == "constructor") engine = new SharedEngine(settings);
                else if (route == "ordinary") { engine.BeginTrans().Should().BeTrue(); engine.Rollback(); }
                else if (route == "fallback") { using var result = engine.Query("docs", new Query()); result.Read().Should().BeTrue(); }
                else if (route == "pinned") engine.Update("docs", new[] { Row(1, 0) });
                else engine.Dispose();
                hits.Should().Be(route == "final-close" ? 0 : 1,
                    "final-close deliberately defers abandoned recovery; other entries acquire the real byte");
                if (route == "fallback") snapshotFailures.Should().Be(1);
                if (route == "final-close")
                {
                    using var probe = new SharedRangeLocks(files.Filename + "-shm");
                    Action stillHeld = probe.EnterWrite;
                    stillHeld.Should().Throw<IOException>();
                }
            }
            finally
            {
                SharedRangeLocks.WriteRetry = null;
                blocked.ExitWrite();
                anchor?.Dispose(); engine?.Dispose();
                remote.Send("close");
                await remote.Expect("done"); await remote.Finish();
            }
            files.WriteIsFree();
            using var cold = new LiteDatabase(files.Filename);
            Verify(cold, route == "final-close" ? 1 : 0);
        }
    }
}
#endif
