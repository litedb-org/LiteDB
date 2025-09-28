namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Provides the well-known structured message kinds emitted by repros.
/// </summary>
public static class ReproHostMessageTypes
{
    /// <summary>
    /// Identifies structured log messages emitted by repros.
    /// </summary>
    public const string Log = "log";

    /// <summary>
    /// Identifies structured result messages emitted by repros.
    /// </summary>
    public const string Result = "result";

    /// <summary>
    /// Identifies lifecycle notifications emitted by repros.
    /// </summary>
    public const string Lifecycle = "lifecycle";

    /// <summary>
    /// Identifies progress updates emitted by repros.
    /// </summary>
    public const string Progress = "progress";
}
