using System.Reflection;
using LiteDB;

// SIGKILL probe: stdout is a handshake; the parent never allows Dispose.
internal static class GroupCommitReviewHarness
{
    internal static bool Run(string filename, string phase)
    {
        using var db = new LiteDatabase(filename);
        db.CheckpointSize = 0;
        var rows = db.GetCollection("rows");
        rows.EnsureIndex("value");
        rows.Insert(new BsonDocument { ["_id"] = 0, ["value"] = 0 });
        db.Checkpoint();
        using var firstBarrier = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondBarrier = new ManualResetEventSlim();
        using var neverReleaseSecond = new ManualResetEventSlim();
        using var appended = new CountdownEvent(4);
        var barrier = 0;
        Action<string> hook = point =>
        {
            if (point == "wal-after-index-confirmation") appended.Signal();
            if (point != "wal-before-durable-flush") return;
            if (Interlocked.Increment(ref barrier) == 1)
            {
                firstBarrier.Set();
                if (!releaseFirst.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("first barrier release");
            }
            else
            {
                secondBarrier.Set();
                if (!neverReleaseSecond.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("parent failed to kill child");
            }
        };
        typeof(LiteDatabase).Assembly.GetType("LiteDB.Engine.EngineState")!
            .GetField("SimulateProcessCrash", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, hook);
        Task Start(int id) => Task.Factory.StartNew(() =>
        {
            rows.Insert(new BsonDocument { ["_id"] = id, ["value"] = id });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var first = Start(1);
        if (!firstBarrier.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("leader did not capture its ticket");
        var followers = Enumerable.Range(2, 3).Select(Start).ToArray();
        if (!appended.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("followers did not append");
        if (phase == "after-first-ack")
        {
            releaseFirst.Set();
            if (!first.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("leader did not acknowledge");
            if (!secondBarrier.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("followers did not reach barrier");
        }
        else if (phase != "before-first-sync") throw new ArgumentException(phase);
        if (followers.Any(task => task.IsCompleted)) throw new InvalidOperationException("follower acknowledged before its barrier");
        Console.WriteLine("ready");
        Console.Out.Flush();
        Console.ReadLine();
        throw new InvalidOperationException("parent must SIGKILL this probe");
    }
}
