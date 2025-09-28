using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Represents a structured input sent to a repro process via STDIN.
/// </summary>
public sealed class ReproInputEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

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

    public T? DeserializePayload<T>()
    {
        if (Payload is not { } payload)
        {
            return default;
        }

        return payload.Deserialize<T>(ReproJsonOptions.Default);
    }

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
    public required string RunIdentifier { get; init; }

    public required string SharedDatabaseRoot { get; init; }

    public int InstanceIndex { get; init; }

    public int TotalInstances { get; init; }

    public string? ManifestId { get; init; }
}

/// <summary>
/// Well-known input envelope types understood by repros.
/// </summary>
public static class ReproInputTypes
{
    public const string HostReady = "host-ready";
}
