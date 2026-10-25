using LiteDB.Engine;

namespace LiteDB.Fuzz.Targets;

internal sealed class TransactionGateFuzzer : IFuzzTarget
{
    public string Name => "transaction-gate";
    public string Description => "Reader lease accounting, foreign release, retired owners, and exclusive admission.";

    public Task RunAsync(FuzzContext context)
    {
        var released = 0;
        while (context.Next())
        {
            using var gate = new TransactionGate();
            var owners = context.Random.Next(1, 9);
            var leases = new List<Thread>();
            for (var owner = 0; owner < owners; owner++)
            {
                var count = context.Random.Next(1, 5);
                context.Trace("reader-owner", new { owner, count });
                FuzzThread.Run(() =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        context.Check(gate.TryEnterReadLock(TimeSpan.Zero), "Reader admission failed.");
                        leases.Add(Thread.CurrentThread);
                    }
                    context.Check(gate.RecursiveReadCount == count, "Recursive lease count disagreed with the model.");
                    var rejected = false;
                    try { gate.TryEnterWriteLock(TimeSpan.Zero); }
                    catch (LockRecursionException) { rejected = true; }
                    context.Check(rejected, "A live reader owner was allowed to upgrade.");
                });
            }
            FuzzThread.CollectRetiredThreads();
            while (leases.Count > 0)
            {
                var index = context.Random.Next(leases.Count);
                var owner = leases[index];
                context.Trace("foreign-release", new { index, remaining = leases.Count });
                FuzzThread.Run(() =>
                {
                    context.Check(!gate.IsReadLockHeld, "New thread inherited a retired reader's identity.");
                    context.Check(!gate.TryEnterWriteLock(TimeSpan.Zero), "Checkpoint passed live readers.");
                    // Releasing another owner's lease must preserve this thread's own lease.
                    context.Check(gate.TryEnterReadLock(TimeSpan.Zero), "Foreign reader admission failed.");
                    gate.ExitReadLock(owner);
                    context.Check(gate.RecursiveReadCount == 1, "Foreign release removed the caller's lease.");
                    gate.ExitReadLock(Thread.CurrentThread);
                });
                leases.RemoveAt(index);
                context.Check(gate.CurrentReadCount == leases.Count, "Total leases disagreed with the model.");
                released++;
            }
            FuzzThread.Run(() =>
            {
                context.Check(gate.TryEnterWriteLock(TimeSpan.Zero), "Last release left checkpoint unavailable.");
                try
                {
                    FuzzThread.Run(() =>
                    {
                        context.Check(!gate.TryEnterReadLock(TimeSpan.Zero), "Reader passed an exclusive owner.");
                        context.Check(!gate.TryEnterWriteLock(TimeSpan.Zero), "Two exclusive owners were admitted.");
                    });
                }
                finally { gate.ExitWriteLock(); }
            });
            context.ObserveNovelty("reader-lease-model", owners, released % 4);
        }
        context.Metrics["foreignLeasesReleased"] = released;
        return Task.CompletedTask;
    }
}
