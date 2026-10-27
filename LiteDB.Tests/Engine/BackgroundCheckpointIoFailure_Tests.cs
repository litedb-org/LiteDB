#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Issues;
using Xunit;

namespace LiteDB.Tests.Engine
{
    [Collection(NativeFileSyncCollection.Name)]
    public class BackgroundCheckpointIoFailure_Tests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (var password in new[] { null, "secret" })
                foreach (var fallback in new[] { false, true })
                    foreach (var mode in new[] { "data-write", "log-sync", "journal-sync", "data-sync", "salt-sync" })
                        yield return new object[] { password, fallback, mode };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task WorkerIoFailureWakesAdmissionAndRefusesReleasedWriterAcknowledgement(
            string password, bool fallback, string mode)
        {
            var scenario = new BackgroundCheckpointIntegrationScenario(password, durable: true);
            try
            {
                var worker = scenario.Engine.InstallDirectCheckpointLifetime();
                var state = BackgroundCheckpointBoundaryMatrix_Tests.State(scenario.Engine);
                var peer = new LiteEngine(new EngineSettings { Filename = scenario.File.Filename, Password = password });
                using var pass = new ManualResetEventSlim();
                using var releasePass = new ManualResetEventSlim();
                using var released = new ManualResetEventSlim();
                using var releaseAck = new ManualResetEventSlim();
                using var waiting = new ManualResetEventSlim();
                Thread writerThread = null;
                Exception injected = null;
                var faults = 0;
                var targetSyncs = 0;
                var postStopIo = 0;
                worker.BeforeCheckpointWorkerPass = () => { pass.Set(); AutomaticCheckpointWorker_Tests.Wait(releasePass); };
                scenario.Engine.GetMonitor().AfterTransactionExit = () =>
                {
                    if (!ReferenceEquals(Thread.CurrentThread, writerThread)) return;
                    released.Set();
                    AutomaticCheckpointWorker_Tests.Wait(releaseAck);
                };
                scenario.Engine.SimulateDataWriteFail = _ =>
                {
                    if (state.Stopped) Interlocked.Increment(ref postStopIo);
                    if (!worker.IsWorkerThread || mode != "data-write") return;
                    Interlocked.Increment(ref faults);
                    injected = WriteFailure.InFile(new IOException("ENOSPC during background data write"), FileOrigin.Data);
                    throw injected;
                };
                NativeFileSync.SimulateErrno = path =>
                {
                    if (state.Stopped) Interlocked.Increment(ref postStopIo);
                    var target = mode == "log-sync" || mode == "journal-sync" ? NativeFileSyncCollection.LogPath(scenario.File.Filename)
                        : NativeFileSyncCollection.CanonicalPath(scenario.File.Filename);
                    if (!worker.IsWorkerThread || mode == "data-write" || path != target) return 0;
                    if (Interlocked.Increment(ref targetSyncs) < (mode == "journal-sync" || mode == "salt-sync" ? 2 : 1)) return 0;
                    Interlocked.Increment(ref faults);
                    return mode == "log-sync" || mode == "journal-sync" ? 28 : 5;
                };
                Task<Exception> writer = null, successor = null;
                try
                {
                    for (var i = 1; i <= 3; i++) scenario.Database.UserVersion = i;
                    AutomaticCheckpointWorker_Tests.Wait(pass);
                    for (var i = 4; i <= 7; i++) scenario.Database.UserVersion = i;
                    writer = DirectCheckpointLifetime_Tests.Start(() =>
                    {
                        writerThread = Thread.CurrentThread;
                        scenario.Database.BeginTrans().Should().BeTrue();
                        scenario.Database.GetCollection("rows").Insert(BackgroundCheckpointScenario.Row(3));
                        var transaction = scenario.Engine.GetMonitor().GetThreadTransaction();
                        if (fallback) transaction.Pages.Commit += _ => { };
                        scenario.Database.Commit().Should().BeTrue();
                    });
                    AutomaticCheckpointWorker_Tests.Wait(released);
                    scenario.Disk.WriteAdmission.UnderPressure.Should().BeTrue();
                    scenario.Disk.WriteAdmission.Writers.Should().Be(0);
                    scenario.Disk.WriteAdmission.BeforeCheckpointWriterWait = waiting.Set;
                    successor = DirectCheckpointLifetime_Tests.Start(() => peer.BeginTrans());
                    AutomaticCheckpointWorker_Tests.Wait(waiting);
                    successor.IsCompleted.Should().BeFalse();
                    releasePass.Set();
                    SpinWait.SpinUntil(() => state.Stopped, TimeSpan.FromSeconds(5)).Should().BeTrue();
                    var cause = state.TerminalCause;
                    if (mode == "data-write") cause.Should().BeSameAs(injected);
                    else cause.Should().BeOfType<FileSyncException>();
                    state.WriteFailure.Should().NotBeNull();
                    state.WriteFailure.Cause.Should().BeSameAs(cause);
                    state.WriteFailure.WalKept.Should().BeTrue();
                    state.WriteFailure.File.Should().Be(mode == "log-sync" || mode == "journal-sync" ? "log" : "data");
                    releaseAck.Set();
                    await DirectCheckpointLifetime_Tests.Completes(writer);
                    await DirectCheckpointLifetime_Tests.Completes(successor);
                    writer.Result.Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                    successor.Result.Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                    Record.Exception(() => peer.Query("rows", new Query())).Should().BeOfType<IOException>()
                        .Which.InnerException.Should().BeSameAs(cause);
                    Record.Exception(() => scenario.Engine.Checkpoint()).Should().BeOfType<IOException>()
                        .Which.InnerException.Should().BeSameAs(cause);
                    Record.Exception(() => { using var refused = new LiteEngine(new EngineSettings
                        { Filename = scenario.File.Filename, Password = password }); }).Should().BeOfType<IOException>()
                        .Which.InnerException.Should().BeSameAs(cause);
                    Record.Exception(peer.Dispose).Should().BeSameAs(cause);
                    Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
                    Record.Exception(scenario.Engine.Dispose).Should().BeSameAs(cause);
                    worker.HasExited.Should().BeTrue();
                    faults.Should().Be(1, "the failed pass must never be retried");
                    postStopIo.Should().Be(0, "terminal cleanup cannot start another write or sync");
                }
                finally
                {
                    releasePass.Set();
                    releaseAck.Set();
                    if (writer != null) await DirectCheckpointLifetime_Tests.Completes(writer);
                    if (successor != null) await DirectCheckpointLifetime_Tests.Completes(successor);
                    Record.Exception(peer.Dispose);
                    Record.Exception(scenario.Engine.Dispose);
                    NativeFileSync.SimulateErrno = null;
                }
                for (var open = 0; open < 3; open++)
                {
                    using (var db = new LiteDatabase(new ConnectionString { Filename = scenario.File.Filename, Password = password }))
                    {
                        db.UserVersion.Should().Be(7);
                        var expected = Enumerable.Range(0, 4).Select(BackgroundCheckpointScenario.Row).ToArray();
                        db.GetCollection("rows").FindAll().Should().BeEquivalentTo(expected);
                        foreach (var id in Enumerable.Range(0, 5))
                            db.GetCollection("rows").Find(Query.EQ("value", id * 7)).Should()
                                .BeEquivalentTo(expected.Where(row => row["_id"].AsInt32 == id));
                        db.GetCollection("sentinel").FindById(1)["payload"].AsString.Should().Be("untouched");
                        db.Checkpoint();
                    }
                    AllocationReachability.Verify(scenario.File.Filename, password);
                }
            }
            finally { Record.Exception(scenario.Dispose); }
        }
    }
}
#endif
