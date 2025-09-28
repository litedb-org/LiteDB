using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Represents a structured input sent to a repro process via STDIN.
/// </summary>
public sealed class ReproInputEnvelope
{
    /// <summary>
    /// Gets or sets the message type identifier.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Gets or sets the time the envelope was created.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the optional payload associated with the envelope.
    /// </summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Creates a host-ready envelope that signals the repro host is ready to receive work.
    /// </summary>
    /// <param name="runIdentifier">The unique identifier for the run.</param>
    /// <param name="sharedDatabaseRoot">The shared database directory assigned by the host.</param>
    /// <param name="instanceIndex">The index of the current instance.</param>
    /// <param name="totalInstances">The total number of instances participating in the run.</param>
    /// <param name="manifestId">The identifier of the manifest being executed.</param>
    /// <returns>The constructed host-ready envelope.</returns>
    public static ReproInputEnvelope CreateHostReady(string runIdentifier, string sharedDatabaseRoot, int instanceIndex, int totalInstances, string? manifestId)
    {
        var payload = new ReproHostReadyPayload
        {
            RunIdentifier = runIdentifier,
            SharedDatabaseRoot = sharedDatabaseRoot,
            InstanceIndex = instanceIndex,
            TotalInstances = totalInstances,
            ManifestId = manifestId
        };

        return new ReproInputEnvelope
        {
            Type = ReproInputTypes.HostReady,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(payload, ReproJsonOptions.Default)
        };
    }

    /// <summary>
    /// Deserializes the payload to a strongly typed value.
    /// </summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <returns>The deserialized payload, or <c>null</c> when no payload is available.</returns>
    public T? DeserializePayload<T>()
    {
        if (Payload is not { } payload)
        {
            return default;
        }

        return payload.Deserialize<T>(ReproJsonOptions.Default);
    }

    /// <summary>
    /// Attempts to parse an input envelope from its JSON representation.
    /// </summary>
    /// <param name="line">The JSON text to parse.</param>
    /// <param name="envelope">When this method returns, contains the parsed envelope if successful.</param>
    /// <param name="error">When this method returns, contains the parsing error if parsing failed.</param>
    /// <returns><c>true</c> if the envelope was parsed successfully; otherwise, <c>false</c>.</returns>
    public static bool TryParse(string? line, out ReproInputEnvelope? envelope, out JsonException? error)
    {
        envelope = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<ReproInputEnvelope>(line, ReproJsonOptions.Default);
            return envelope is not null;
        }
        catch (JsonException jsonException)
        {
            error = jsonException;
            return false;
        }
    }
}

/// <summary>
/// Describes the payload attached to <see cref="ReproInputTypes.HostReady"/>.
/// </summary>
public sealed record ReproHostReadyPayload
{
    /// <summary>
    /// Gets or sets the unique run identifier assigned by the host.
    /// </summary>
    public required string RunIdentifier { get; init; }

    /// <summary>
    /// Gets or sets the shared database directory assigned by the host.
    /// </summary>
    public required string SharedDatabaseRoot { get; init; }

    /// <summary>
    /// Gets or sets the zero-based instance index.
    /// </summary>
    public int InstanceIndex { get; init; }

    /// <summary>
    /// Gets or sets the total number of instances participating in the run.
    /// </summary>
    public int TotalInstances { get; init; }

    /// <summary>
    /// Gets or sets the identifier of the manifest being executed.
    /// </summary>
    public string? ManifestId { get; init; }
}

/// <summary>
/// Well-known input envelope types understood by repros.
/// </summary>
public static class ReproInputTypes
{
    /// <summary>
    /// Identifies an envelope notifying that the host is ready.
    /// </summary>
    public const string HostReady = "host-ready";
}
