#if NET8_0_OR_GREATER
using System;
using System.Threading;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileWarmCounter_Tests
    {
        [OneFileTheory]
        [InlineData(false)]
        [InlineData(true)]
        public void Admission_counter_has_a_cold_control_and_excludes_cache_gate_contention_and_deliberate_pacing(bool pressure)
        {
            using var files = new SharedOneFilePublicationFixture();
            using var engine = new SharedEngine(files.Settings);
            using var db = new LiteDatabase(engine, disposeOnClose: false);
            SharedCoordinationDiagnostics.AdmissionOperations = 0;
            using (var cold = engine.Query("docs", new Query { Limit = 1 })) cold.Read().Should().BeTrue();
            SharedCoordinationDiagnostics.AdmissionOperations.Should().BeGreaterThan(0, "cold protected admission is the positive control");
            SharedOneFileWarmReads_Tests.Warm(engine, db);
            engine.ForceCoordinatedYield = pressure;
            var pressureHits = 0;
            engine.CoordinationStage = stage => { if (stage == "writer-pressure") Interlocked.Increment(ref pressureHits); };
            using var started = new ManualResetEventSlim();
            Exception error = null;
            var operations = -1;
            var admissionOperations = -1;
            var thread = new Thread(() =>
            {
                try
                {
                    SharedCoordinationDiagnostics.AdmissionOperations = 0;
                    started.Set();
                    using (var reader = engine.Query("docs", new Query()))
                    {
                        admissionOperations = SharedCoordinationDiagnostics.AdmissionOperations;
                        var count = 0;
                        while (reader.Read()) count++;
                        count.Should().Be(160);
                    }
                    operations = SharedCoordinationDiagnostics.AdmissionOperations;
                }
                catch (Exception ex) { error = ex; }
            });
            var hits = engine.CoordinatedReadHits;
            lock (engine.MappedReaderTable.ReadCache.Gate)
            {
                thread.Start();
                started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5))
                    .Should().BeTrue("the caller must actually contend for the held cache gate");
                thread.IsAlive.Should().BeTrue();
            }
            thread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            error.Should().BeNull();
            admissionOperations.Should().Be(0, "selected admission seams exclude contention and Sleep/Yield, which can issue syscalls");
            operations.Should().Be(1, "stream completion separately probes the empty WAL for checkpoint cleanup");
            engine.CoordinatedReadHits.Should().Be(hits + 1);
            pressureHits.Should().Be(pressure ? 1 : 0);
            Verify(db, 0);
        }
    }
}
#endif
