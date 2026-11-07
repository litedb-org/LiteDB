using System.Diagnostics;

namespace Ab5021Benchmarks;

// Process-wide counters include allocations on durable writer threads.
internal sealed class ScenarioMeasurement
{
    private readonly Stopwatch _watch = new();
    private readonly int _gen0;
    private readonly int _gen1;
    private readonly int _gen2;
    private readonly long _allocated;

    public ScenarioMeasurement()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _allocated = GC.GetTotalAllocatedBytes(precise: true);
        _watch.Start();
    }

    public void Stop()
    {
        _watch.Stop();
        AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - _allocated;
        Gen0 = GC.CollectionCount(0) - _gen0;
        Gen1 = GC.CollectionCount(1) - _gen1;
        Gen2 = GC.CollectionCount(2) - _gen2;
    }

    public double Milliseconds => _watch.Elapsed.TotalMilliseconds;
    public int Gen0 { get; private set; }
    public int Gen1 { get; private set; }
    public int Gen2 { get; private set; }
    public long AllocatedBytes { get; private set; }
}
