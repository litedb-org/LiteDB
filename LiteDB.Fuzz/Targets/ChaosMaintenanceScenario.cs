using LiteDB.Engine;
using LiteDB.Tests.Safety;
using LiteDB.Utils;

namespace LiteDB.Fuzz.Targets;

/// <summary>
/// One forced interleaving of an active operation (thread "active") with a close, rebuild or fatal
/// I/O failure (thread "maintenance") on the same connection. Forced points (lazy input, reader
/// position, explicit transaction between calls, checkpoint stage, exclusive admission) pause one
/// thread while the other starts; the coordinator waits until that thread finished or arrived at a
/// wait (admission hooks, Shared mutex waiters, the Shared dispose drain marker) before releasing.
/// What that fixes is listed in <see cref="Schedule"/>; which side wins once both run is left to
/// native scheduling and judged only against the declared outcome sets.
/// </summary>
internal sealed partial class ChaosMaintenanceScenario : IDisposable
{
    internal static readonly TimeSpan OpDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JoinBound = OpDeadline + TimeSpan.FromSeconds(15);
    private const string Payload = "payload";

    private readonly FuzzContext _context;
    private readonly MaintenancePlan _plan;
    private readonly ForcedPoint _activePoint = new("active");
    private readonly ForcedPoint _maintenancePoint = new("maintenance");
    private readonly FaultInjection _fault = new("chaos-maintenance-wal-write", FaultModel.Skip);
    private ScenarioThread _active;
    private ScenarioThread _maintenance;
    private EngineSettings _settings;
    private ILiteEngine _engine;
    private LiteDatabase _db;
    private volatile bool _fatalAdmitted;
    private volatile bool _faultArmed;
    private bool _observedBlocked;

    internal ChaosMaintenanceScenario(FuzzContext context, MaintenancePlan plan, string file)
    {
        _context = context;
        _plan = plan;
        this.File = file;
    }

    internal string File { get; }
    internal DurableLedger Ledger { get; } = new();
    /// <summary>Writes whose outcome is uncertain: on cold reopen each group shows all of its "before" or all of its "after" documents.</summary>
    internal List<UncertainWrite> Uncertain { get; } = new();
    internal List<string> Schedule { get; } = new();
    internal IEnumerable<OpRecord> Records => _active.Records.Concat(_maintenance.Records);
    internal ILiteEngine Engine => _engine;
    internal LiteDatabase Database => _db;
    internal bool ActiveFinished => _active.Finished;
    internal bool MaintenanceFinished => _maintenance.Finished;
    internal Exception InjectedFault => _fault.Injected;
    internal bool FaultRequired => _fatalAdmitted;
    /// <summary>Set by the coordinator's concurrent second Dispose (close, maintenance first).</summary>
    internal SecondDisposeObservation SecondDispose { get; private set; }

    internal void Open()
    {
        _settings = new EngineSettings { Filename = this.File, CheckpointStage = this.OnCheckpointStage };
        _engine = _plan.Shared ? new SharedEngine(_settings) : new LiteEngine(_settings);
        _db = new LiteDatabase(_engine);
        var seed = Enumerable.Range(1, _plan.SeedRows).Select(id => Row(id, id)).ToArray();
        _db.GetCollection("rows").Insert(seed);
        // The fatal write targets an existing collection: creating one would need the header first.
        var anchor = Row(0, 0);
        _db.GetCollection("fatal").Insert(anchor);
        this.Ledger.Acknowledge("fatal", 0, anchor);
        // Rows a bulk upsert rewrites are judged by its own outcome (acknowledged, untouched or uncertain).
        var rewritten = _plan.Active == ActiveKind.Bulk && _plan.Upsert ? _plan.Writes : 0;
        foreach (var row in seed.Skip(rewritten)) this.Ledger.Acknowledge("rows", row["_id"], row);
        if (_engine is LiteEngine direct) this.InstallDirectHooks(direct);
    }

    /// <summary>Run the interleaving; returns once both threads stopped (or throws a harness failure).</summary>
    internal void Run()
    {
        _active = new ScenarioThread("active", this.ActiveBody);
        _maintenance = new ScenarioThread("maintenance", this.MaintenanceBody);
        _activePoint.Thread = _active.Thread;
        _maintenancePoint.Thread = _maintenance.Thread;
        using var drain = new MarkerSignal("maintenance:shared-dispose-during-active-call", _maintenance);
        try
        {
            if (_plan.Order == MaintenanceOrder.ActiveFirst) this.ActiveFirst();
            else this.MaintenanceFirst();
        }
        finally
        {
            _activePoint.Release();
            _maintenancePoint.Release();
            var joined = _active.Join(JoinBound) & _maintenance.Join(JoinBound);
            if (!joined)
                throw new FuzzFailureException("CHAOS_MAINTENANCE_JOIN",
                    $"A scenario thread did not stop within {JoinBound.TotalSeconds:F0} s ({_plan.Dimension}).");
        }
        foreach (var thread in new[] { _active, _maintenance })
            if (thread.Failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(thread.Failure).Throw();
        foreach (var point in new[] { _activePoint, _maintenancePoint })
            if (point.TimedOut)
                throw new FuzzFailureException("CHAOS_MAINTENANCE_FORCED_POINT_" + FuzzOracles.Safe(point.Name),
                    $"The {point.Name} forced point was never released ({_plan.Dimension}).");
    }

    private void ActiveFirst()
    {
        _active.Start();
        this.Expect("active-paused", () => _activePoint.IsReached || _active.Finished);
        _maintenance.Start();
        this.Expect("maintenance-engaged", () => _maintenance.Finished || this.Engaged(_maintenance));
        _activePoint.Release();
    }

    private void MaintenanceFirst()
    {
        _maintenance.Start();
        this.Expect("maintenance-paused", () => _maintenancePoint.IsReached || _maintenance.Finished);
        _active.Start();
        this.Expect("active-engaged", () => _active.Finished || _activePoint.IsReached || this.Engaged(_active));
        if (_plan.Maintenance == MaintenanceKind.Close) this.DisposeAgain();
        _maintenance.Blocked = false;
        _maintenancePoint.Release();
        this.Expect("maintenance-resumed", () => _maintenance.Finished || _maintenance.Blocked || this.WaitsBehindPausedWal(_maintenance));
        _activePoint.Release();
    }

    /// <summary>
    /// The caller's own Dispose, concurrent with the paused one: records when it returned relative to
    /// the first close and which files were still open then (the coordinator judges it later).
    /// </summary>
    private void DisposeAgain()
    {
        var thread = new ScenarioThread("second-dispose", () => { });
        var returned = thread.Call(_context, "SecondDispose", () => _db.Dispose(), _plan.Dimension,
            ChaosMaintenanceDeclarations.SecondDispose, OpDeadline);
        var gaps = new List<string>();
        var full = Path.GetFullPath(this.File);
        var handles = QuiescentProbe.OpenHandles(full, gaps) ?? Array.Empty<string>();
        // Probed from a fresh thread, so no recursion of a thread here can make a held mutex look free.
        var mutexHeld = _plan.Shared &&
            !QuiescentProbe.TryAcquire(LiteDB.Client.Shared.SharedMutexNameFactory.Create(full, SharedMutexNameStrategy.Default), out _);
        this.SecondDispose = new SecondDisposeObservation(thread.Records[0], _maintenancePoint.IsReached && !_maintenance.Finished,
            handles, gaps.Count > 0, mutexHeld);
        this.Schedule.Add(returned ? "second-dispose-returned" : "second-dispose-threw");
    }

    /// <summary>
    /// The thread arrived at a wait that the paused side makes it block on: a hook or marker saw it, or
    /// (Shared) the connection's mutex has a waiter while the paused side holds the mutex.
    /// </summary>
    private bool Engaged(ScenarioThread thread) => thread.Blocked || this.WaitsBehindPausedWal(thread) ||
        _engine is SharedEngine shared && this.PausedSideHoldsMutex(thread) && SharedInternals.MutexWaiters(shared) > 0;

    /// <summary>
    /// The active write is paused inside its WAL write and still holds the commit's header and WAL
    /// writer locks, which page allocation, a commit and the close checkpoint also take, and none of
    /// those sites has a hook. So the coordinator observes the waiting thread itself: blocked
    /// (WaitSleepJoin) without interruption for <see cref="ScenarioThread.BlockedWindow"/>, longer than
    /// any timed wait on that path (exclusive admission waits 10 ms). The only other thread holding
    /// anything is the paused one, so such a wait is a wait behind it (stack dumps in the M2b report).
    /// </summary>
    private bool WaitsBehindPausedWal(ScenarioThread thread)
    {
        if (!this.ActivePausedInWal || ReferenceEquals(thread, _active) || _maintenancePoint.IsPaused ||
            !thread.BlockedFor(ScenarioThread.BlockedWindow)) return false;
        _observedBlocked = true;
        return true;
    }

    /// <summary>Shared: the other side, paused at its forced point, owns the writer mutex (a reader result does not).</summary>
    private bool PausedSideHoldsMutex(ScenarioThread waiter) =>
        !ReferenceEquals(waiter, _maintenance) || _plan.Active != ActiveKind.Reader;

    /// <summary>Direct: the other side, paused at its forced point, holds exclusive admission.</summary>
    private bool PausedSideHoldsExclusive(ScenarioThread waiter) => ReferenceEquals(waiter, _maintenance)
        ? _plan.Active is ActiveKind.Checkpoint or ActiveKind.Rebuild
        : _plan.Maintenance is MaintenanceKind.Rebuild or MaintenanceKind.Close;

    /// <summary>The active write is paused inside its WAL write, holding the header and WAL writer locks.</summary>
    private bool ActivePausedInWal => _plan.PausesInWal && _activePoint.IsPaused;

    private void Expect(string edge, Func<bool> condition)
    {
        if (!ScenarioWait.Until(condition, ForcedPoint.Bound))
            throw new FuzzFailureException("CHAOS_MAINTENANCE_FORCED_POINT_" + FuzzOracles.Safe(edge),
                $"Forced edge {edge} was not reached within {ForcedPoint.Bound.TotalSeconds:F0} s ({_plan.Dimension}).");
        this.Schedule.Add(edge + (_observedBlocked ? "/observed-blocked" : "") + (_active.Finished ? "/active-done" : "") +
            (_maintenance.Finished ? "/maintenance-done" : ""));
        _observedBlocked = false;
    }

    private void InstallDirectHooks(LiteEngine engine)
    {
        engine.SimulateBeforeTransactionAdmission = this.OnTransactionAdmission;
        engine.SimulateBeforeExclusiveAdmission = this.OnExclusiveAdmission;
        engine.SimulateAfterExclusiveAdmission = this.OnExclusiveAdmitted;
        engine.SimulateDiskWriteFail = this.OnWalWrite;
    }

    private ScenarioThread Current => _active?.IsCurrent == true ? _active : _maintenance?.IsCurrent == true ? _maintenance : null;

    /// <summary>Admission blocks only behind exclusive admission (a checkpoint or rebuild holds it).</summary>
    private void OnTransactionAdmission()
    {
        var thread = this.Current;
        if (thread != null && this.PausedSideHoldsExclusive(thread)) thread.Blocked = true;
    }

    /// <summary>Exclusive admission waits for every transaction, and the paused side always holds one.</summary>
    private void OnExclusiveAdmission()
    {
        var thread = this.Current;
        if (thread != null) thread.Blocked = true;
    }

    /// <summary>
    /// Every WAL page write of the engine (or Shared core) it is installed on: the active write pauses
    /// at its first one; the armed fatal write fails at its first one (skip model: the write never happens).
    /// </summary>
    private void OnWalWrite(PageBuffer page)
    {
        if (_plan.PausesInWal) _activePoint.Pause();
        if (_faultArmed && _maintenance?.IsCurrent == true)
            _fault.Run(() => { }, () => new IOException("Injected chaos-maintenance WAL write failure."));
    }

    /// <summary>Rebuild holds exclusive admission and has not closed the engine yet.</summary>
    private void OnExclusiveAdmitted()
    {
        if (_plan.Active == ActiveKind.Rebuild) _activePoint.Pause();
        if (_plan.Maintenance == MaintenanceKind.Rebuild && _plan.Order == MaintenanceOrder.MaintenanceFirst) _maintenancePoint.Pause();
    }

    /// <summary>A checkpoint holds (or failed to get) exclusive admission and has not taken the commit lock yet.</summary>
    private void OnCheckpointStage(string stage)
    {
        if (stage != "before-commit-lock") return;
        if (_plan.Active == ActiveKind.Checkpoint) _activePoint.Pause();
        if (_plan.Maintenance == MaintenanceKind.Close && _plan.Order == MaintenanceOrder.MaintenanceFirst) _maintenancePoint.Pause();
    }

    internal static BsonDocument Row(int id, int value) => new()
    {
        ["_id"] = id, ["value"] = value, [Payload] = new string((char)('a' + id % 26), 3000)
    };

    public void Dispose()
    {
        _activePoint.Dispose();
        _maintenancePoint.Dispose();
    }
}

/// <summary>
/// A write whose outcome is uncertain (it raced a close, rebuild or fatal stop and threw): on cold
/// reopen its documents show all of <see cref="Before"/> or all of <see cref="After"/> (null: absent).
/// </summary>
internal sealed record UncertainWrite(string Collection, BsonValue[] Ids, BsonDocument[] Before, BsonDocument[] After);

/// <summary>What the coordinator saw when its own Dispose returned while the first close was paused.</summary>
internal sealed record SecondDisposeObservation(OpRecord Record, bool FirstStillClosing, string[] OpenHandles, bool HandlesUnknown,
    bool MutexHeld);
