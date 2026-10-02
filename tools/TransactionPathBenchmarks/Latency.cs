using System;
using System.Diagnostics;
using System.Numerics;

/// <summary>
/// Log-bucketed latency histogram over <see cref="Stopwatch"/> ticks: 16 linear sub-buckets per
/// power of two (at most 1/16 relative error). Buckets are preallocated, so recording never
/// allocates and the measured operation is the only source of allocation in a timed loop.
/// </summary>
internal sealed class LatencyHistogram
{
    private const int SubBits = 4;
    private const int Sub = 1 << SubBits;
    private const int BucketCount = (64 - SubBits) * Sub;
    private readonly long[] _counts = new long[BucketCount];
    private long _total;
    private long _max;

    public long Count => _total;

    public void Record(long ticks)
    {
        if (ticks < 0) ticks = 0;
        _counts[Index(ticks)]++;
        _total++;
        if (ticks > _max) _max = ticks;
    }

    public void Merge(LatencyHistogram other)
    {
        for (var i = 0; i < BucketCount; i++) _counts[i] += other._counts[i];
        _total += other._total;
        if (other._max > _max) _max = other._max;
    }

    /// <summary>The upper bound of the bucket holding the quantile, never above the observed maximum.</summary>
    public long ValueAt(double quantile)
    {
        if (_total == 0) return 0;
        var rank = (long)Math.Ceiling(quantile * _total);
        if (rank < 1) rank = 1;
        long seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += _counts[i];
            if (seen >= rank) return Math.Min(UpperBound(i), _max);
        }
        return _max;
    }

    public void WriteTo(JsonRecord record)
    {
        record.Add("p50us", Micros(ValueAt(0.50))).Add("p90us", Micros(ValueAt(0.90)))
            .Add("p99us", Micros(ValueAt(0.99))).Add("p999us", Micros(ValueAt(0.999)))
            .Add("maxUs", Micros(_max)).Add("operations", _total);
    }

    private static double Micros(long ticks) => Math.Round(ticks * 1_000_000.0 / Stopwatch.Frequency, 3);

    private static int Index(long value)
    {
        if (value < Sub) return (int)value;
        var shift = 63 - BitOperations.LeadingZeroCount((ulong)value) - SubBits;
        return ((shift + 1) << SubBits) + (int)((value >> shift) & (Sub - 1));
    }

    private static long UpperBound(int index)
    {
        if (index < Sub) return index;
        var shift = (index >> SubBits) - 1;
        var lower = (long)(Sub + (index & (Sub - 1))) << shift;
        return lower + (1L << shift) - 1;
    }
}

/// <summary>GC collection counts and allocated bytes over a measured interval.</summary>
internal readonly struct GcWindow
{
    private readonly long _allocated;
    private readonly int _gen0, _gen1, _gen2;

    private GcWindow(long allocated, int gen0, int gen1, int gen2)
    {
        _allocated = allocated;
        _gen0 = gen0;
        _gen1 = gen1;
        _gen2 = gen2;
    }

    public static GcWindow Start() =>
        new GcWindow(GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    /// <summary>The allocation and collections since <see cref="Start"/>.</summary>
    public GcWindow Stop() => new GcWindow(GC.GetTotalAllocatedBytes(true) - _allocated,
        GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1, GC.CollectionCount(2) - _gen2);

    /// <summary>Writes bytesPerOperation and the collection counts of a stopped window.</summary>
    public void WriteTo(JsonRecord record, long operations) =>
        record.Add("bytesPerOperation", _allocated / (double)Math.Max(1, operations))
            .Add("gen0", _gen0).Add("gen1", _gen1).Add("gen2", _gen2);
}

internal static class Latency
{
    public static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// Warm up, then run timed windows on the calling thread. Every operation's latency goes into
    /// a preallocated histogram; each window's rate is its operations over its measured length.
    /// </summary>
    public static void Measure(Action operation, RunSettings settings, JsonRecord record, string kind = "latency")
    {
        var histogram = new LatencyHistogram();
        var rates = new double[settings.Windows];
        var windowTicks = Ticks(settings.Window);
        var warmEnd = Stopwatch.GetTimestamp() + Ticks(settings.Warmup);
        while (Stopwatch.GetTimestamp() < warmEnd) operation();
        var gc = GcWindow.Start();
        for (var w = 0; w < rates.Length; w++)
        {
            long count = 0;
            var start = Stopwatch.GetTimestamp();
            var end = start + windowTicks;
            var now = start;
            while (now < end)
            {
                var began = now;
                operation();
                now = Stopwatch.GetTimestamp();
                histogram.Record(now - began);
                count++;
            }
            rates[w] = count * (double)Stopwatch.Frequency / (now - start);
        }
        gc.Stop().WriteTo(record, histogram.Count);
        record.Add("kind", kind).Add("opsPerSecond", Bench.Median(rates)).Add("windows", rates);
        histogram.WriteTo(record);
    }
}
