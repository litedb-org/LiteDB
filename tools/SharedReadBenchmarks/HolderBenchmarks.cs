using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using LiteDB;

internal static class HolderBenchmarks
{
    // Isolate holder creation/ownership from engine open, durable commit and checkpoint.
    // Native acquisition/release still use the actual production pin protocol.
    internal static void Run(int count)
    {
        var assembly = typeof(LiteDatabase).Assembly;
        var pinType = assembly.GetType("LiteDB.Client.Shared.SharedMutexPin", true);
        var turnType = assembly.GetType("LiteDB.Client.Shared.SharedMutexTurnstile", true);
        using var mutex = new Mutex();
        using var turn = new Mutex();
        var turnstile = Activator.CreateInstance(turnType, new object[] { turn });
        var closeType = typeof(Action<,>).MakeGenericType(pinType, typeof(bool));
        var close = Delegate.CreateDelegate(closeType,
            typeof(HolderBenchmarks).GetMethod(nameof(Close), BindingFlags.Static | BindingFlags.NonPublic));
        var acquire = pinType.GetMethod("Acquire");
        var exit = pinType.GetMethod("Exit");
        var ready = pinType.GetMethod("MarkReady");
        var request = pinType.GetMethod("RequestRelease");
        var released = pinType.GetMethod("WaitReleased");
        var parameters = new object[] { mutex, turnstile, (Func<bool>)(() => false), close,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) };
        void Operation()
        {
            var pin = acquire.Invoke(null, parameters);
            ready.Invoke(pin, new object[] { TimeSpan.Zero });
            exit.Invoke(pin, new object[] { false });
            request.Invoke(pin, new object[] { false });
            released.Invoke(pin, null);
        }
        for (var i = 0; i < 100; i++) Operation();
        var samples = new double[count];
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        for (var i = 0; i < count; i++)
        {
            var start = Stopwatch.GetTimestamp();
            Operation();
            samples[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
        var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        Array.Sort(samples);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            scenario = "holder", count, meanMs = samples.Average(), p50Ms = samples[count / 2],
            p95Ms = samples[(int)(count * .95)], p99Ms = samples[(int)(count * .99)],
            worstMs = samples[count - 1], cpuMsPerOperation = cpuMs / count,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        }));
    }

    private static void Close(object pin, bool abandoned) { }
}
