#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;
using static LiteDB.Tests.Internals.DirectHostGroupCommit_Tests;

namespace LiteDB.Tests.Internals
{
    [Collection(NativeFileSyncCollection.Name)]
    public class DirectHostGroupStop_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Uncertain_writer_poisoning_reaches_pending_tickets_on_every_facade(bool syncStarted, bool ioFailure)
        {
            using var file = new TempFile();
            Seed(file.Filename);
            using (var leaderEngine = new LiteEngine(file.Filename))
            using (var leaderDb = new LiteDatabase(leaderEngine, disposeOnClose: false))
            using (var waiterDb = new LiteDatabase(file.Filename))
            using (var failingDb = new LiteDatabase(file.Filename))
            {
                var state = (EngineState)typeof(LiteEngine).GetField("_state",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(leaderEngine);
                using var captured = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var noop = new ManualResetEventSlim();
                var syncs = 0;
                var directorySyncs = 0;
                var acknowledgements = 0;
                Task<Exception> leader = null, waiter = null;
                state.AtCrashPoint = phase =>
                {
                    if (!syncStarted && phase == "wal-before-durable-flush")
                    {
                        captured.Set();
                        Wait(release);
                    }
                };
                NativeFileSync.SimulateErrno = path =>
                {
                    if (path != FileHelper.GetLogFile(file.Filename)) return 0;
                    Interlocked.Increment(ref syncs);
                    if (syncStarted) { captured.Set(); Wait(release); }
                    return 0;
                };
                NativeFileSync.SimulateDirectoryErrno = path => { Interlocked.Increment(ref directorySyncs); return 0; };
                Exception hookFailure = null;
                var teardownWindow = false;
                try
                {
                    leader = Start(() =>
                    {
                        leaderDb.GetCollection("rows").Insert(Row(1));
                        Interlocked.Increment(ref acknowledgements);
                    });
                    Wait(captured);
                    waiter = Start(() =>
                    {
                        waiterDb.BeginTrans().Should().BeTrue();
                        waiterDb.GetCollection("rows").Delete(99).Should().BeFalse();
                        noop.Set();
                        waiterDb.Commit().Should().BeTrue();
                        Interlocked.Increment(ref acknowledgements);
                    });
                    Wait(noop);
                    state.AtCrashPoint = phase =>
                    {
                        if (phase == "wal-confirmation-after-write")
                            throw ioFailure ? new IOException("cross-facade confirmation") : new InvalidOperationException("cross-facade confirmation");
                    };
                    state.AfterFailedWalWrite = () =>
                    {
                        try
                        {
                            teardownWindow = true;
                            state.Stopped.Should().BeTrue();
                            release.Set();
                            Task.WaitAll(new Task[] { leader, waiter }, TimeSpan.FromSeconds(15)).Should().BeTrue();
                            leader.Result.Should().BeAssignableTo<IOException>();
                            waiter.Result.Should().BeAssignableTo<IOException>();
                            foreach (var facade in new[] { leaderDb, waiterDb, failingDb })
                            {
                                Action later = () => facade.GetCollection("rows").Insert(Row(3));
                                later.Should().Throw<IOException>();
                            }
                        }
                        catch (Exception error) { hookFailure = error; }
                    };
                    Action uncertain = () => failingDb.GetCollection("rows").Insert(Row(2));
                    uncertain.Should().Throw<IOException>();
                    await Completes(Task.WhenAll(leader, waiter));
                    teardownWindow.Should().BeTrue("pending facades must fail before the writer starts teardown");
                    hookFailure.Should().BeNull();
                    acknowledgements.Should().Be(0);
                    syncs.Should().Be(syncStarted ? 1 : 0);
                    directorySyncs.Should().Be(0);
                    state.Stopped.Should().BeTrue();
                    leader.Result.ToString().Should().Contain("cross-facade confirmation");
                    waiter.Result.ToString().Should().Contain("cross-facade confirmation");
                    Action attach = () => { using var peer = new LiteDatabase(file.Filename); };
                    attach.Should().Throw<IOException>();
                }
                finally
                {
                    release.Set();
                    try
                    {
                        if (leader != null) await Completes(leader);
                        if (waiter != null) await Completes(waiter);
                    }
                    finally
                    {
                        state.AtCrashPoint = null;
                        state.AfterFailedWalWrite = null;
                        NativeFileSync.SimulateErrno = null;
                        NativeFileSync.SimulateDirectoryErrno = null;
                    }
                }
            }
            VerifyRecovery(file.Filename, new[] { 0 });
        }
    }
}
#endif
