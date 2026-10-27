using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Direct;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class BackgroundCheckpointOpenFailure_Tests
    {
        [Fact]
        public void FailedAttachmentNotificationCannotFinalizeAnotherFacadesHostReference()
        {
            using var file = new TempFile();
            var engine = new LiteEngine(new EngineSettings
                { Filename = file.Filename, DurableCommits = false, TransactionPageLimit = 2 });
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            var cause = new IOException("attachment worker start failed");
            try
            {
                BackgroundCheckpointIntegrationScenario.Seed(db, 2);
                var worker = engine.InstallDirectCheckpointLifetime();
                var starts = 0;
                worker.BeforeCheckpointWorkerStart = () => { starts++; throw cause; };
                db.BeginTrans().Should().BeTrue();
                db.GetCollection("rows").Insert(Enumerable.Range(3, 60).Select(BackgroundCheckpointScenario.Row));
                BackgroundCheckpointIntegrationScenario.DiskOf(engine).WriteAdmission.UnderPressure.Should().BeTrue();
                FailedAttach(file.Filename).Should().BeSameAs(cause);
                starts.Should().Be(1);
                SpinWait.SpinUntil(() => worker.HasExited, TimeSpan.FromSeconds(5)).Should().BeTrue();
                // Finalize the failed constructor's lifetime while the first
                // facade is still owned. Its native/registry reference must stay.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                DirectFacadeLifetime.Drain();
                Record.Exception(() => { using var refused = new LiteEngine(new EngineSettings
                    { Filename = file.Filename, DurableCommits = false, TransactionPageLimit = 2 }); })
                    .Should().BeOfType<IOException>().Which.InnerException.Should().BeSameAs(cause);
                Record.Exception(engine.Dispose).Should().BeSameAs(cause);
                GC.KeepAlive(engine);
            }
            finally { Record.Exception(engine.Dispose); }
            BackgroundCheckpointIntegrationScenario.VerifyRecovery(file.Filename, null, 0);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Exception FailedAttach(string filename) => Record.Exception(() =>
        {
            using var failed = new LiteEngine(new EngineSettings
                { Filename = filename, DurableCommits = false, TransactionPageLimit = 2 });
        });
    }
}
