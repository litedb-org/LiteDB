using Xunit;

namespace LiteDB.Tests.Engine
{
    public class TransactionHandleHandoffProgress_Tests
    {
        [Fact]
        public void Peer_successes_and_own_retries_do_not_renew_a_stalled_worker()
        {
            var progress = new TransactionHandoffProgress(2, 1000);
            progress.Succeeded(0, 0);
            progress.Attempt(0);
            progress.Rejected(0);
            progress.Succeeded(1, 14999);
            Assert.Null(progress.Timeout(14999));
            var failure = progress.Timeout(15000);
            Assert.Contains("Worker 0 made no successful handoff", failure);
            Assert.Contains("worker=0, completed=1/1000, rejected=1, lastSuccessMs=0", failure);
            Assert.False(progress.AllCompleted);
        }

        [Fact]
        public void Monitoring_detects_an_operation_blocked_inside_Run()
        {
            var progress = new TransactionHandoffProgress(1, 1000);
            progress.Succeeded(0, 1000);
            progress.Attempt(0);
            Assert.Null(progress.Timeout(15999));
            Assert.Contains("stage=inside Run", progress.Timeout(16000));
        }

        [Fact]
        public void Successful_work_renews_only_its_progress_budget_not_the_hard_cap()
        {
            var progress = new TransactionHandoffProgress(1, 1000);
            foreach (var time in new long[] { 10000, 20000, 30000, 40000, 50000, 59999 })
            {
                progress.Succeeded(0, time);
                Assert.Null(progress.Timeout(time));
            }
            Assert.Contains("Handoff total limit (60s)", progress.Timeout(60000));
            Assert.Contains("lastSuccessMs=59999", progress.Describe(60000));
        }

        [Fact]
        public void Completed_worker_cannot_hide_an_unstarted_peer()
        {
            var progress = new TransactionHandoffProgress(2, 1);
            progress.Succeeded(0, 1);
            Assert.Contains("Worker 1 made no successful handoff", progress.Timeout(15000));
            Assert.Contains("stage=not scheduled", progress.Describe(15000));
            progress.Succeeded(1, 15001);
            Assert.True(progress.AllCompleted);
        }
    }
}
