using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointNotificationFailure_Tests
    {
        [Theory]
        [InlineData(null, "thread-start")]
        [InlineData("secret", "thread-start")]
        [InlineData(null, "data-page")]
        [InlineData("secret", "data-page")]
        public void AutomaticNotificationRetainsCauseAndWholeCommittedState(string password, string boundary)
        {
            using var file = new TempFile();
            var engine = new LiteEngine(new EngineSettings
            {
                Filename = file.Filename, Password = password, DurableCommits = false
            });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var cause = new IOException("automatic notification " + boundary);
            var hits = 0;
            try
            {
                BackgroundCheckpointIntegrationScenario.Seed(db, 2);
                var worker = engine.InstallDirectCheckpointLifetime();
                if (boundary == "thread-start") worker.BeforeCheckpointWorkerStart = () =>
                {
                    engine.GetMonitor().Transactions.Should().BeEmpty();
                    BackgroundCheckpointIntegrationScenario.DiskOf(engine).WriteAdmission.Writers.Should().Be(0);
                    Interlocked.Increment(ref hits);
                    throw cause;
                };
                else
                {
                    worker.BeforeCheckpointWorkerPass = () => { entered.Set(); AutomaticCheckpointWorker_Tests.Wait(release); };
                    engine.CheckpointStage = stage =>
                    {
                        if (stage != "data-page") return;
                        Interlocked.Increment(ref hits);
                        throw cause;
                    };
                }
                db.UserVersion = 1;
                db.UserVersion = 2;
                var commitError = Record.Exception(() => db.UserVersion = 3);
                if (boundary == "thread-start") commitError.Should().BeSameAs(cause);
                else
                {
                    commitError.Should().BeNull("the transaction acknowledges while the worker is held before its pass");
                    AutomaticCheckpointWorker_Tests.Wait(entered);
                    release.Set();
                }
                SpinWait.SpinUntil(() => worker.HasExited || worker.State == AutomaticCheckpointState.Faulted,
                    TimeSpan.FromSeconds(5)).Should().BeTrue();
                hits.Should().Be(1);
                Record.Exception(() => engine.Checkpoint()).Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(() => db.UserVersion = 4).Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(() => { using var refused = new LiteEngine(new EngineSettings
                    { Filename = file.Filename, Password = password, DurableCommits = false }); })
                    .Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(engine.Dispose).Should().BeSameAs(cause);
                Record.Exception(engine.Dispose).Should().BeSameAs(cause);
            }
            finally { release.Set(); Record.Exception(engine.Dispose); }
            BackgroundCheckpointIntegrationScenario.VerifyRecovery(file.Filename, password, 3);
        }
    }
}
