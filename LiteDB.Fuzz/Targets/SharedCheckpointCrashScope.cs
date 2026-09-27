using LiteDB.Client.Shared;

namespace LiteDB.Fuzz.Targets;

/// <summary>Keep checkpoint work available until the configured crash hook can consume it.</summary>
internal sealed class SharedCheckpointCrashScope : IDisposable
{
    private readonly SharedMutexOwner _owner;

    private SharedCheckpointCrashScope(SharedEngine engine)
    {
        _owner = engine.MutexOwner;
        _owner.Enter();
    }

    /// <summary>
    /// Retain the crash worker's ownership across commit and checkpoint. Otherwise a peer
    /// can empty the WAL in that gap and the selected checkpoint hook will never run.
    /// Peers still contend through the real shared mutex and recover after process death.
    /// Other crash boundaries keep their ordinary operation-by-operation interleavings.
    /// </summary>
    internal static SharedCheckpointCrashScope Create(SharedEngine engine, string crashPoint) =>
        crashPoint?.StartsWith("checkpoint-", StringComparison.Ordinal) == true ? new(engine) : null;

    public void Dispose()
    {
        _owner.Exit();
        _owner.WaitForRelease();
    }
}
