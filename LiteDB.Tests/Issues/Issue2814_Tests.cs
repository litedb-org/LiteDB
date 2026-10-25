using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2814_Tests
    {
        [Fact]
        public void Finite_concurrent_readers_do_not_allow_WAL_growth_far_beyond_checkpoint_budget()
        {
            using var file = new TempFile();
            long peak = 0;
            using (var db = new LiteDatabase(file.Filename))
            using (var stop = new CancellationTokenSource())
            using (var ready = new CountdownEvent(4))
            {
                db.CheckpointSize = 100;
                var col = db.GetCollection("rows");
                col.Insert(new BsonDocument { ["_id"] = 0, ["payload"] = "reader control" });
                db.Checkpoint();
                var errors = new ConcurrentQueue<Exception>();
                long reads = 0;
                // Dedicated threads: a saturated thread pool (parallel test classes on a slow
                // runner) must not delay the readers' start past the handshake deadline.
                var readers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            col.FindById(0)["payload"].AsString.Should().Be("reader control");
                            Interlocked.Increment(ref reads);
                        }
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                var log = Path.ChangeExtension(file.Filename, null) + "-log.db";
                try
                {
                    ready.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("all four readers must be running before the writes");
                    for (var id = 1; id <= 1000; id++)
                    {
                        col.Insert(new BsonDocument { ["_id"] = id, ["payload"] = new string((char)('A' + id % 26), 4000) });
                        peak = Math.Max(peak, File.Exists(log) ? new FileInfo(log).Length : 0);
                    }
                }
                finally
                {
                    stop.Cancel();
                    Task.WaitAll(readers, TimeSpan.FromSeconds(30)).Should().BeTrue();
                }
                errors.Should().BeEmpty();
                reads.Should().BeGreaterThan(100);
            }
            using var reopened = new LiteDatabase(file.Filename);
            var rows = reopened.GetCollection("rows");
            rows.Count().Should().Be(1001);
            for (var id = 1; id <= 1000; id++) rows.FindById(id)["payload"].AsString.Should().Be(new string((char)('A' + id % 26), 4000));
            // Verify the acknowledged writes even when the WAL growth assertion fails.
            // Four soft-limit windows plus four windows of scheduling/write slack.
            peak.Should().BeLessOrEqualTo(8L * 100 * 8192, "auto-checkpoint needs bounded escalation under continuous short readers");
        }
    }
}
