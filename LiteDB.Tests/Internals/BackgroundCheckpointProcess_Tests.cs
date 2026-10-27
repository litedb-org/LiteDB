#if !NETFRAMEWORK
using System.Collections.Generic;
using System.Threading.Tasks;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class BackgroundCheckpointProcess_Tests
    {
        public static IEnumerable<object[]> Boundaries() => BackgroundCheckpointFaultMatrix.ProcessCases();

        [Theory]
        [MemberData(nameof(Boundaries))]
        public async Task KilledWorkerPreservesAcknowledgedTransactions(string password, bool durable, string stage, bool partial)
        {
            using var file = new TempFile();
            using (var child = new MvccProcess("bg-worker", file.Filename, password, stage + "|" + durable + "|" + partial))
            {
                await child.Expect("ack:" + (partial ? 3 : 1));
                child.Send("continue");
                await child.Expect("hit:" + stage);
                await child.Kill();
            }
            BackgroundCheckpointRecovery.Verify(file.Filename, password, partial ? 3 : 1);
        }

        [Theory]
        [InlineData(null, false, "bg-queued", "queued")]
        [InlineData("secret", false, "bg-queued", "queued")]
        [InlineData(null, true, "bg-queued", "queued")]
        [InlineData("secret", true, "bg-queued", "queued")]
        [InlineData(null, false, "bg-waiter", "waiter")]
        [InlineData("secret", false, "bg-waiter", "waiter")]
        [InlineData(null, true, "bg-waiter", "waiter")]
        [InlineData("secret", true, "bg-waiter", "waiter")]
        [InlineData(null, false, "bg-uncommitted", "data-page")]
        [InlineData("secret", false, "bg-uncommitted", "data-page")]
        [InlineData(null, true, "bg-uncommitted", "data-page")]
        [InlineData("secret", true, "bg-uncommitted", "data-page")]
        [InlineData(null, false, "bg-close", "checkpoint-before-data-flush")]
        [InlineData("secret", false, "bg-close", "checkpoint-before-data-flush")]
        [InlineData(null, true, "bg-close", "checkpoint-before-data-flush")]
        [InlineData("secret", true, "bg-close", "checkpoint-before-data-flush")]
        public async Task KillWithQueuedWorkWaitingWriterOrFinalDrain(string password, bool durable, string mode, string stage)
        {
            using var file = new TempFile();
            using (var child = new MvccProcess(mode, file.Filename, password, stage + "|" + durable))
            {
                await child.Expect("ack:1");
                child.Send("continue");
                if (mode == "bg-close") await child.Expect("closing");
                await child.Expect("hit:" + stage);
                await child.Kill();
            }
            BackgroundCheckpointRecovery.Verify(file.Filename, password, 1);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("secret")]
        public async Task RecoveryCanBeKilledTwiceAfterWorkerDeath(string password)
        {
            using var file = new TempFile();
            using (var worker = new MvccProcess("bg-worker", file.Filename, password, "data-page@4|True"))
            {
                await worker.Expect("ack:1");
                worker.Send("continue");
                await worker.Expect("hit:data-page@4");
                await worker.Kill();
            }
            foreach (var phase in new[] { "promotion-recovery-after-header-flush", "promotion-recovery-before-journal-retire" })
            {
                using var repair = new MvccProcess("bg-repair", file.Filename, password, phase + "|True");
                await repair.Expect("hit:" + phase);
                await repair.Kill();
            }
            BackgroundCheckpointRecovery.Verify(file.Filename, password, 1);
        }
    }
}
#endif
