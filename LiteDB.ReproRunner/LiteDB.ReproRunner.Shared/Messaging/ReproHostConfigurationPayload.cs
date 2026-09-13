using System.Text.Json.Serialization;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Represents the configuration handshake payload emitted by repro processes.
/// </summary>
public sealed record class ReproHostConfigurationPayload
{
    /// <summary>
    /// Gets or sets a value indicating whether the repro was built against the source project.
    /// </summary>
    [JsonPropertyName("useProjectReference")]
    public required bool UseProjectReference { get; init; }

    /// <summary>
    /// Gets or sets the LiteDB package version used by the repro when built from NuGet.
    /// </summary>
    [JsonPropertyName("liteDBPackageVersion")]
    public string? LiteDBPackageVersion { get; init; }
}
