using System.Reflection;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// A forced point: the first time the bound thread reaches it, it reports that and waits until the
/// coordinator releases it. Library callbacks (lazy input, lock and checkpoint hooks) call
/// <see cref="Pause"/>; it never throws into the library. A release that never comes is recorded
/// (<see cref="TimedOut"/>) and turned into a harness failure by the coordinator.
/// </summary>
internal sealed class ForcedPoint : IDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly ManualResetEventSlim _reached = new(false);
    private readonly ManualResetEventSlim _release = new(false);
    private int _fired;

    internal ForcedPoint(string name) => this.Name = name;

    internal string Name { get; }
    internal Thread Thread { get; set; }
    internal bool IsReached => _reached.IsSet;
    /// <summary>The bound thread is waiting at this point right now.</summary>
    internal bool IsPaused => _paused;
    internal bool TimedOut { get; private set; }
    private volatile bool _paused;

    internal void Pause()
    {
        if (this.Thread == null || !ReferenceEquals(Thread.CurrentThread, this.Thread)) return;
        if (Interlocked.Exchange(ref _fired, 1) != 0) return;
        _paused = true;
        _reached.Set();
        if (!_release.Wait(Bound)) this.TimedOut = true;
        _paused = false;
    }

    internal void Release() => _release.Set();

    public void Dispose()
    {
        _release.Set();
        _reached.Dispose();
        _release.Dispose();
    }
}

/// <summary>One public call of a scenario thread and its normalized outcome.</summary>
internal sealed record OpRecord(string Role, string Op, string[] Permitted, string Outcome, Exception Error)
{
    /// <summary><c>ok</c>, or <c>threw|refused:Type#code</c> (code only for LiteException).</summary>
    internal string Label => this.Error == null ? "ok" : $"{this.Outcome}:{TypeLabel(this.Error)}";

    internal bool IsPermitted => ChaosMaintenanceDeclarations.Matches(this.Permitted, this.Outcome, this.Error);

    internal static string TypeLabel(Exception error) => error is LiteException lite
        ? $"{error.GetType().FullName}#{lite.ErrorCode}" : error.GetType().FullName;
}

/// <summary>
/// A dedicated thread of a scenario (never a pool thread: explicit transactions and Shared mutex
/// recursion are thread-affine, and a reused thread would carry state into the next scenario).
/// </summary>
internal sealed class ScenarioThread
{
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly Thread _thread;

    internal ScenarioThread(string role, Action body)
    {
        this.Role = role;
        _thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception error) { this.Failure = error; }
            finally { _finished.Set(); }
        }) { IsBackground = true, Name = "chaos-maintenance " + role };
    }

    internal string Role { get; }
    internal Thread Thread => _thread;
    internal List<OpRecord> Records { get; } = new();
    internal bool Finished => _finished.IsSet;
    /// <summary>A hook saw this thread arrive at a wait (admission, exclusive admission, Shared mutex).</summary>
    internal volatile bool Blocked;
    /// <summary>A harness failure (an oracle or deadline failure), never an operation's outcome.</summary>
    internal Exception Failure { get; private set; }

    internal void Start() => _thread.Start();

    internal bool Join(TimeSpan bound) => _finished.Wait(bound);

    /// <summary>How long a thread must stay blocked before the coordinator counts it as waiting behind the paused side.</summary>
    internal static readonly TimeSpan BlockedWindow = TimeSpan.FromMilliseconds(200);
    private long _blockedSince = -1;

    /// <summary>
    /// Coordinator only: the thread has been in WaitSleepJoin (a contended lock, a wait or a sleep)
    /// without interruption for at least <paramref name="window"/>, judged over successive calls.
    /// </summary>
    internal bool BlockedFor(TimeSpan window)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((_thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
        {
            _blockedSince = -1;
            return false;
        }
        if (_blockedSince < 0) _blockedSince = now;
        return System.Diagnostics.Stopwatch.GetElapsedTime(_blockedSince, now) >= window;
    }

    internal bool IsCurrent => ReferenceEquals(Thread.CurrentThread, _thread);

    /// <summary>Run one public call under its deadline; record the outcome; false when it threw.</summary>
    internal bool Call(FuzzContext context, string op, Action call, string dimension, string[] permitted, TimeSpan deadline)
    {
        var refusals = context.Oracles.RefusalsOnThread;
        try
        {
            context.Deadline(op, call, dimension, deadline, permitted);
            this.Records.Add(new OpRecord(this.Role, op, permitted, "ok", null));
            return true;
        }
        catch (FuzzFailureException) { throw; }
        catch (Exception error)
        {
            var outcome = context.Oracles.RefusalsOnThread != refusals ? "refused" : "threw";
            this.Records.Add(new OpRecord(this.Role, op, permitted, outcome, error));
            return false;
        }
    }
}

/// <summary>Coordinator waits: every wait is bounded and reports whether its condition held.</summary>
internal static class ScenarioWait
{
    internal static bool Until(Func<bool> condition, TimeSpan bound)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > bound) return false;
            Thread.Sleep(1);
        }
        return true;
    }
}

/// <summary>
/// Private state of a Shared connection the coordinator observes (read by reflection, like the
/// M1 probes; a renamed field throws instead of silently reading nothing).
/// </summary>
internal static class SharedInternals
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Threads of this connection waiting for its writer mutex.</summary>
    internal static int MutexWaiters(SharedEngine engine) => (int)Field("_mutexWaiters").GetValue(engine);

    /// <summary>The operation core attached right now (the current call's engine), or null.</summary>
    internal static LiteEngine Core(SharedEngine engine) => (LiteEngine)Field("_engine").GetValue(engine);

    /// <summary>The settings every core of this connection opens with.</summary>
    internal static EngineSettings Settings(SharedEngine engine) => (EngineSettings)Field("_settings").GetValue(engine);

    private static FieldInfo Field(string name) => typeof(SharedEngine).GetField(name, Private)
        ?? throw new InvalidOperationException($"SharedEngine.{name} no longer exists; update the chaos-maintenance harness.");
}

/// <summary>
/// Signals a marker hit on a given thread through the single <see cref="Reachability.Observer"/>
/// slot, chained to the oracle state's observer (which must already be installed) and restored on dispose.
/// </summary>
internal sealed class MarkerSignal : IDisposable
{
    private readonly Action<string> _previous;
    private readonly string _marker;
    private readonly ScenarioThread _thread;

    internal MarkerSignal(string marker, ScenarioThread thread)
    {
        _marker = marker;
        _thread = thread;
        _previous = Reachability.Observer;
        Reachability.Observer = this.OnMarker;
    }

    private void OnMarker(string marker)
    {
        _previous?.Invoke(marker);
        if (marker == _marker && _thread.IsCurrent) _thread.Blocked = true;
    }

    public void Dispose() => Reachability.Observer = _previous;
}
