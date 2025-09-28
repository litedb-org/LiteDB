namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Provides the well-known structured message kinds emitted by repros.
/// </summary>
public static class ReproHostMessageTypes
{
    public const string Log = "log";
    public const string Result = "result";
    public const string Lifecycle = "lifecycle";
    public const string Progress = "progress";
}
