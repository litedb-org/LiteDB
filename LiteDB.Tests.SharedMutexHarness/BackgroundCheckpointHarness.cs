using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using LiteDB.Tests.Engine;

internal static class BackgroundCheckpointHarness
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static bool TryRun(string mode, string filename, string? password, string[] args)
    {
        if (!mode.StartsWith("bg-", StringComparison.Ordinal)) return false;
        var options = args[4].Split('|');
        var target = options[0];
        var durable = options[1] == "True";
        var partial = options.Length > 2 && options[2] == "True";
        var settings = new EngineSettings { Filename = filename, Password = password, DurableCommits = durable,
            TransactionPageLimit = mode == "bg-uncommitted" ? 1 : 1000 };
        Action<string> repair = phase => { if (phase == target) Hold("hit:" + target); };
        if (mode == "bg-repair")
        {
            typeof(LiteEngine).Assembly.GetType("LiteDB.Engine.EngineState")!
                .GetField("SimulateProcessCrash", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, repair);
            using var recovering = new LiteEngine(settings);
            throw new InvalidOperationException("Recovery hook was not reached: " + target);
        }

        using var engine = new LiteEngine(settings);
        using var db = new LiteDatabase(engine, disposeOnClose: false);
        BackgroundCheckpointWorkload.Seed(db);
        var worker = typeof(LiteEngine).GetMethod("InstallDirectCheckpointLifetime", Hidden)!.Invoke(engine, null)!;
        using var pause = (IDisposable)worker.GetType().GetMethod("Pause", Hidden)!.Invoke(worker, null)!;
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var armed = false;
        var hits = 0;
        var pieces = target.Split('@');
        var occurrence = pieces.Length == 1 ? 1 : int.Parse(pieces[1]);
        Action<string> observe = phase =>
        {
            if (armed && phase == pieces[0] && Interlocked.Increment(ref hits) == occurrence)
                Hold("hit:" + target);
        };
        SetHook(engine, "CheckpointStage", observe);
        var unconfirmedPages = 0;
        SetHook(engine, "SimulateCrashPoint", (Action<string>)(phase =>
        {
            if (phase == "wal-page-after-write") Interlocked.Increment(ref unconfirmedPages);
            observe(phase);
        }));
        SetHook(worker, "BeforeCheckpointWorkerPass", (Action)(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Parent did not release worker");
            observe("BeforeCheckpointWorkerPass");
        }));
        SetHook(worker, "AfterCheckpointWorkerPass", (Action)(() => observe("AfterCheckpointWorkerPass")));

        // Partial reclamation requires three committed WAL generations and a
        // reader pinned between the first and the latest. Keep it on its owner.
        db.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(1));
        var readerReady = new ManualResetEventSlim();
        if (partial)
        {
            new Thread(() =>
            {
                using var reader = engine.Query("rows", new Query());
                if (!reader.Read()) throw new InvalidOperationException("Reader did not pin a generation");
                readerReady.Set();
                Thread.Sleep(Timeout.Infinite);
            }) { IsBackground = true }.Start();
            if (!readerReady.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Reader did not start");
            db.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(2));
            db.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(3));
        }
        if (mode == "bg-uncommitted")
        {
            var before = Volatile.Read(ref unconfirmedPages);
            using var staged = new ManualResetEventSlim();
            new Thread(() =>
            {
                db.BeginTrans();
                db.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(99));
                if (Volatile.Read(ref unconfirmedPages) <= before)
                    throw new InvalidOperationException("Uncommitted transaction did not emit safepoints");
                staged.Set();
                Thread.Sleep(Timeout.Infinite);
            }) { IsBackground = true }.Start();
            if (!staged.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("No uncommitted safepoint output");
        }
        db.CheckpointSize = 2;
        Console.WriteLine("ack:" + (partial ? 3 : 1));
        if (Console.ReadLine() != "continue") throw new InvalidOperationException("Missing parent command");
        armed = true;
        if (mode == "bg-queued") Hold("hit:queued");
        pause.Dispose();
        if (!entered.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Worker did not start");
        if (mode == "bg-waiter")
        {
            var disk = typeof(LiteEngine).GetField("_disk", Hidden)!.GetValue(engine)!;
            var admission = disk.GetType().GetProperty("WriteAdmission", Hidden)!.GetValue(disk)!;
            SetHook(admission, "BeforeCheckpointWriterWait", (Action)(() => Hold("hit:waiter")));
            // Must wait at H before even enumerating the supplied documents.
            db.GetCollection("rows").Update(BackgroundCheckpointWorkload.Rows(99));
            throw new InvalidOperationException("High-water writer unexpectedly returned");
        }
        if (mode == "bg-close")
        {
            var close = Task.Run(() => engine.Dispose());
            if (!SpinWait.SpinUntil(() => worker.GetType().GetProperty("State", Hidden)!.GetValue(worker)!.ToString() == "Closing",
                TimeSpan.FromSeconds(20))) throw new TimeoutException("Final close did not enter drain");
            if (close.IsCompleted) throw new InvalidOperationException("Close returned before worker drain");
            Console.WriteLine("closing");
        }
        release.Set();
        Thread.Sleep(Timeout.Infinite);
        return true;
    }

    private static void SetHook(object owner, string name, object hook)
    {
        var property = owner.GetType().GetProperty(name, Hidden);
        if (property != null) property.SetValue(owner, hook);
        else owner.GetType().GetField(name, Hidden)!.SetValue(owner, hook);
    }

    private static void Hold(string marker)
    {
        Console.WriteLine(marker);
        Thread.Sleep(Timeout.Infinite);
    }
}
