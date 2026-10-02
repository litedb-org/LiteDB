using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Safety.Tests
{
    /// <summary>
    /// ConnectionClean's judgement of a disposed Shared connection's mutex owner (holder) thread
    /// (<see cref="HolderExitWait"/>): an idle holder that exits late, on a loaded host, is waited for and
    /// recorded; a holder that still owns the OS mutex, or keeps receiving commands, fails.
    /// </summary>
    public class HolderExitWait_Tests
    {
        [Fact]
        public void An_idle_holder_that_outlives_the_design_bound_is_waited_for_and_recorded_as_a_late_exit()
        {
            using (var file = new TempFile())
            {
                var owner = DisposedOwner(file.Filename);
                Assert.True(owner.HasHolderThread, "the holder idles for HolderIdle after its last command");

                // A zero design bound stands in for a host that starved the holder past it.
                var result = HolderExitWait.Wait(owner, TimeSpan.Zero, HolderExitWait.LateExitBound);

                Assert.Null(result.Violation);
                Assert.False(result.Alive);
                Assert.True(result.LateIdleExit);
                Assert.Equal(0, result.Commands);
            }
        }

        [Fact]
        public void A_holder_that_keeps_the_OS_mutex_for_no_owner_fails_at_the_design_bound()
        {
            using (var file = new TempFile())
            {
                var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                var owner = UseHolder(db);
                db.Dispose();
                // The leak: the holder takes the OS mutex for nobody, so its idle rule never lets it exit.
                Assert.True((bool)Send(owner, "TryAcquire"));
                try
                {
                    var result = ConnectionCleanProbe.Evaluate(db);

                    Assert.True(result.HolderThread);
                    Assert.Contains(result.Violations, v => v.StartsWith(
                        "threads: the mutex owner thread holds the OS mutex for no owner", StringComparison.Ordinal));
                    Assert.True(result.WaitedMs < HolderExitWait.LateExitBound.TotalMilliseconds / 2,
                        $"a holder that cannot exit is judged at the design bound, waited {result.WaitedMs:F0} ms");
                }
                finally
                {
                    Send(owner, "Release");
                }
                Assert.Null(HolderExitWait.Wait(owner).Violation);
            }
        }

        [Fact]
        public void Commands_after_dispose_restart_the_design_bound_and_a_holder_that_exits_after_them_is_clean()
        {
            using (var file = new TempFile())
            {
                var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                var owner = UseHolder(db);
                db.Dispose();
                // Calls racing Dispose acquire and release the mutex before their admission refuses them; these
                // commands keep the holder busy past the design bound measured from Dispose, then stop.
                var sender = Sender(owner, TimeSpan.FromSeconds(2.5));
                var result = ConnectionCleanProbe.Evaluate(db);
                sender.Join();

                Assert.Empty(result.Violations);
                Assert.False(result.HolderThread);
                Assert.True(result.WaitedMs > 2500, $"the holder exits only after its last command, waited {result.WaitedMs:F0} ms");
            }
        }

        [Fact]
        public void A_holder_kept_busy_by_commands_past_the_late_bound_fails()
        {
            using (var file = new TempFile())
            {
                var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                var owner = UseHolder(db);
                db.Dispose();
                var sender = Sender(owner, TimeSpan.FromSeconds(6));
                HolderExitResult result;
                try
                {
                    result = HolderExitWait.Wait(owner, QuiescentProbe.Grace, TimeSpan.FromSeconds(3));
                }
                finally
                {
                    sender.Join();
                }

                Assert.True(result.Alive);
                Assert.StartsWith("threads: the mutex owner thread outlived 3000 ms after Dispose; ", result.Violation);
                Assert.Contains("Release@", result.Violation);
                Assert.False(result.LateIdleExit);
                Assert.Null(HolderExitWait.Wait(owner).Violation);
            }
        }

        /// <summary>Sends a release of nothing every 50 ms for <paramref name="duration"/>: each restarts the idle clock and changes no other state.</summary>
        private static Thread Sender(SharedMutexOwner owner, TimeSpan duration)
        {
            var sender = new Thread(() =>
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < duration)
                {
                    Send(owner, "Release");
                    Thread.Sleep(50);
                }
            }) { IsBackground = true, Name = "holder-command-sender" };
            sender.Start();
            return sender;
        }

        private static SharedMutexOwner DisposedOwner(string path)
        {
            var db = new LiteDatabase($"Filename={path};Connection=shared");
            var owner = UseHolder(db);
            db.Dispose();
            return owner;
        }

        /// <summary>An explicit transaction is owned through the holder thread (scoped calls need none).</summary>
        private static SharedMutexOwner UseHolder(LiteDatabase db)
        {
            Assert.True(db.BeginTrans());
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            Assert.True(db.Commit());
            var engine = Assert.IsType<SharedEngine>(ConnectionCleanProbe.EngineOf(db));
            Assert.True(engine.MutexOwner.HasHolderThread, "this test needs a holder thread");
            return engine.MutexOwner;
        }

        private static object Send(SharedMutexOwner owner, string command)
        {
            var type = typeof(SharedMutexOwner);
            var value = Enum.Parse(type.GetNestedType("Command", BindingFlags.NonPublic), command);
            var send = type.GetMethod("Send", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("SharedMutexOwner.Send no longer exists; update this test.");
            try { return send.Invoke(owner, new[] { value }); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
    }
}
