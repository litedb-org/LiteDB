namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Represents the severity of a structured repro log message.
/// </summary>
public enum ReproHostLogLevel
{
    /// <summary>
    /// Verbose diagnostic messages useful for tracing execution.
    /// </summary>
    Trace,

    /// <summary>
    /// Low-level diagnostic messages used for debugging.
    /// </summary>
    Debug,

    /// <summary>
    /// Informational messages describing normal operation.
    /// </summary>
    Information,

    /// <summary>
    /// Indicates a non-fatal issue that may require attention.
    /// </summary>
    Warning,

    /// <summary>
    /// Indicates that an error occurred during execution.
    /// </summary>
    Error,

    /// <summary>
    /// Indicates a critical failure that prevents continued execution.
    /// </summary>
    Critical
}
