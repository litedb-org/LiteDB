using System;
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
                var result = HolderExitWait.Wait(owner, TimeSpan.Zero);

                Assert.Null(result.Violation);
                Assert.False(result.Alive);
                Assert.True(result.LateIdleExit);
                Assert.Equal(0, result.Commands);
            }
        }

        [Fact]
        public void A_holder_that_still_owns_the_OS_mutex_after_dispose_fails_without_the_late_allowance()
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
                    Assert.Contains("threads: the mutex owner thread still holds the OS mutex, so it cannot exit", result.Violations);
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
        public void A_holder_kept_busy_by_commands_after_dispose_fails_at_the_design_bound()
        {
            using (var file = new TempFile())
            using (var stop = new ManualResetEventSlim())
            {
                var db = new LiteDatabase($"Filename={file.Filename};Connection=shared");
                var owner = UseHolder(db);
                db.Dispose();
                // Each command restarts the holder's idle clock; a release of nothing changes no other state.
                var sender = new Thread(() =>
                {
                    while (!stop.Wait(50)) Send(owner, "Release");
                }) { IsBackground = true };
                sender.Start();
                ConnectionCleanResult result;
                try
                {
                    result = ConnectionCleanProbe.Evaluate(db);
                }
                finally
                {
                    stop.Set();
                    sender.Join();
                }

                Assert.True(result.HolderThread);
                Assert.Contains(result.Violations, v => v.StartsWith("threads: the mutex owner thread outlived", StringComparison.Ordinal) &&
                    v.Contains("command(s) after Dispose"));
                Assert.False(result.HolderLateExit);
                Assert.Null(HolderExitWait.Wait(owner).Violation);
            }
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
