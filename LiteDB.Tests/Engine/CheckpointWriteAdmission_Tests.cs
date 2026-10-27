using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class CheckpointWriteAdmission_Tests
    {
        private static CheckpointWriteAdmission Gate(int n = 10, long pages = 0) =>
            new CheckpointWriteAdmission(n, pages * Constants.PAGE_SIZE);
        private static CheckpointWriteAdmission.Permit Take(CheckpointWriteAdmission gate, bool wait = false) =>
            gate.Acquire(TimeSpan.FromSeconds(5), wait, () => { });

        [Fact]
        public void HighWaterEqualityClosesAndOnlyLowWaterReopens()
        {
            var gate = Gate();
            gate.PublishTail(39L * Constants.PAGE_SIZE);
            using (Take(gate)) { }
            gate.PublishTail(40L * Constants.PAGE_SIZE);
            gate.UnderPressure.Should().BeTrue();
            gate.PublishTail(11L * Constants.PAGE_SIZE);
            // Repeated commits at the same pragma must preserve hysteresis.
            gate.Configure(10, 11L * Constants.PAGE_SIZE);
            Action blocked = () => Take(gate);
            blocked.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            gate.PublishTail(10L * Constants.PAGE_SIZE);
            using (Take(gate)) { }
            gate.Writers.Should().Be(0);
        }

        [Fact]
        public void LargePragmaArithmeticDoesNotWrap()
        {
            var gate = Gate(int.MaxValue, 4L * int.MaxValue - 1);
            using (Take(gate)) { }
            gate.PublishTail(4L * int.MaxValue * Constants.PAGE_SIZE);
            gate.UnderPressure.Should().BeTrue();
            gate.PublishTail((long)int.MaxValue * Constants.PAGE_SIZE);
            using (Take(gate)) { }
        }

        [Fact]
        public void PermitsSurviveDisableReenableAndLateDuplicateRelease()
        {
            var gate = Gate();
            var permits = Enumerable.Range(0, 64).Select(_ => Take(gate)).ToList();
            Action saturated = () => Take(gate);
            saturated.Should().Throw<LiteException>();
            gate.Configure(0, 0);
            permits.AddRange(Enumerable.Range(0, 6).Select(_ => Take(gate)));
            gate.Configure(10, 0);
            gate.Writers.Should().Be(70);
            saturated.Should().Throw<LiteException>();
            foreach (var permit in permits.Take(7)) { permit.Dispose(); permit.Dispose(); }
            gate.Writers.Should().Be(63);
            using (Take(gate)) gate.Writers.Should().Be(64);
            foreach (var permit in permits) permit.Dispose();
            gate.Writers.Should().Be(0);
        }

        [Fact]
        public async Task WaiterRemainsBlockedAboveLowWaterThenResumes()
        {
            var gate = Gate(10, 40);
            using var waiting = new ManualResetEventSlim();
            gate.BeforeCheckpointWriterWait = waiting.Set;
            var task = Task.Run(() => Take(gate, wait: true));
            try
            {
                waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                gate.PublishTail(11L * Constants.PAGE_SIZE);
                task.IsCompleted.Should().BeFalse();
                gate.PublishTail(10L * Constants.PAGE_SIZE);
                (await task).Dispose();
                gate.Writers.Should().Be(0);
            }
            finally { gate.Close(); }
        }

        [Fact]
        public async Task SaturatedPermitWaiterResumesAfterRelease()
        {
            var gate = Gate();
            var permits = Enumerable.Range(0, 64).Select(_ => Take(gate)).ToArray();
            using var waiting = new ManualResetEventSlim();
            gate.BeforeCheckpointWriterWait = waiting.Set;
            var task = Task.Run(() => Take(gate, wait: true));
            try
            {
                waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
                gate.Writers.Should().Be(64);
                permits[0].Dispose();
                using (await task) gate.Writers.Should().Be(64);
            }
            finally
            {
                gate.Close();
                foreach (var permit in permits) permit.Dispose();
            }
            gate.Writers.Should().Be(0);
        }

        [Fact]
        public void TimeoutDoesNotConsumePermitAndProgressRestoresAdmission()
        {
            var gate = Gate(10, 40);
            var waits = 0;
            gate.BeforeCheckpointWriterWait = () => waits++;
            Action timeout = () => gate.Acquire(TimeSpan.FromMilliseconds(30), true, () => { });
            timeout.Should().Throw<LiteException>().Which.ErrorCode.Should().Be(LiteException.LOCK_TIMEOUT);
            waits.Should().BeGreaterThan(0);
            gate.Writers.Should().Be(0);
            gate.PublishTail(0);
            using (Take(gate)) { }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StopAndOwnerInvalidationWakeBlockedAdmission(bool terminal)
        {
            var gate = Gate(10, 40);
            using var waiting = new ManualResetEventSlim();
            gate.BeforeCheckpointWriterWait = waiting.Set;
            var valid = 1;
            var cause = new InvalidOperationException("first failure");
            var task = Task.Run(() => Record.Exception(() => gate.Acquire(TimeSpan.FromSeconds(5), true,
                () => { if (Volatile.Read(ref valid) == 0) throw new ObjectDisposedException("owner"); })));
            waiting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            if (terminal) gate.Close(cause);
            else { Volatile.Write(ref valid, 0); gate.Wake(); }
            var error = await task;
            if (terminal) error.Should().BeSameAs(cause);
            else error.Should().BeOfType<ObjectDisposedException>();
            gate.Writers.Should().Be(0);
        }

        [Fact]
        public void PragmaChangesRecomputePressureAndZeroNeverThrottles()
        {
            var gate = Gate(10, 40);
            gate.Configure(20, 40L * Constants.PAGE_SIZE);
            using (Take(gate)) { }
            gate.Configure(5, 40L * Constants.PAGE_SIZE);
            gate.UnderPressure.Should().BeTrue();
            gate.Configure(0, 4000L * Constants.PAGE_SIZE);
            using (Take(gate)) { }
            gate.Configure(5, 4000L * Constants.PAGE_SIZE);
            gate.UnderPressure.Should().BeTrue();
        }
    }
}
