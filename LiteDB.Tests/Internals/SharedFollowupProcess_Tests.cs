#if !NETFRAMEWORK
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedFollowupProcess_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-followup-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");

        public SharedFollowupProcess_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task Killing_a_waiter_while_it_owns_the_turnstile_does_not_abandon_the_database()
        {
            await MvccProcess.Run("seed", Filename, null);
            var name = SharedMutexNameFactory.Create(Filename, SharedMutexNameStrategy.Default);
            using var main = SharedMutexFactory.Create(name);
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() => { main.WaitOne(); held.Set(); release.Wait(); main.ReleaseMutex(); });
            holder.Start();
            held.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            try
            {
                using var waiter = new MvccProcess("followup-wait", Filename, null);
                await waiter.Expect("ready"); // owns Turn, about to wait on main
                await waiter.Kill();
            }
            finally { release.Set(); holder.Join(); }
            await MvccProcess.Run("write", Filename, null, "1");
            using var verify = new MvccProcess("read", Filename, null);
            await verify.Expect("value:1");
            await verify.Finish();
        }

        [Fact]
        public async Task Killing_a_scoped_owner_recovers_acknowledged_data_and_admits_a_queued_process()
        {
            using var owner = new MvccProcess("followup-owner", Filename, null);
            await owner.Expect("ready");
            using var waiter = new MvccProcess("followup-wait", Filename, null);
            await waiter.Expect("ready");
            await owner.Kill();
            await waiter.Expect("done");
            await waiter.Finish();
            using var db = new LiteDatabase(Filename);
            db.GetCollection("ack").FindById(1)["value"].AsInt32.Should().Be(42);
        }

        [Fact]
        public async Task A_pinned_write_loop_yields_to_another_process_before_its_hold_limit()
        {
            await MvccProcess.Run("seed", Filename, null);
            using var owner = new MvccProcess("followup-pin", Filename, null);
            await owner.Expect("ready");
            using var waiter = new MvccProcess("insert", Filename, null, "100");
            var done = await waiter.ReadLine(TimeSpan.FromSeconds(10));
            done.Should().Be("done", "a one-minute pin must yield to the queued writer");
            await waiter.Finish();
            await owner.Kill();
            // Recover in a fresh process through the same Shared coordination protocol,
            // including its mutex-protected recovery, instead of switching to Direct.
            await MvccProcess.Run("followup-verify-pin", Filename, null);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
#endif
