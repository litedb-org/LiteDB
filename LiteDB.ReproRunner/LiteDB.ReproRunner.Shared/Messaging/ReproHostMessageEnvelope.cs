using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Represents a structured message emitted by a repro process.
/// </summary>
public sealed class ReproHostMessageEnvelope
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("level")]
    public ReproHostLogLevel? Level { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("success")]
    public bool? Success { get; init; }

    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("progress")]
    public double? Progress { get; init; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

    public static ReproHostMessageEnvelope CreateLog(string message, ReproHostLogLevel level = ReproHostLogLevel.Information, DateTimeOffset? timestamp = null)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return new ReproHostMessageEnvelope
        {
            Type = ReproHostMessageTypes.Log,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Level = level,
            Text = message
        };
    }

    public static ReproHostMessageEnvelope CreateResult(bool success, string? summary = null, object? payload = null, DateTimeOffset? timestamp = null)
    {
        return new ReproHostMessageEnvelope
        {
            Type = ReproHostMessageTypes.Result,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Success = success,
            Text = summary,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, ReproJsonOptions.Default)
        };
    }

    public static ReproHostMessageEnvelope CreateLifecycle(string stage, object? payload = null, DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            throw new ArgumentException("Lifecycle stage must be provided.", nameof(stage));
        }

        return new ReproHostMessageEnvelope
        {
            Type = ReproHostMessageTypes.Lifecycle,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Event = stage,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, ReproJsonOptions.Default)
        };
    }

    public static ReproHostMessageEnvelope CreateProgress(string stage, double? percentComplete = null, object? payload = null, DateTimeOffset? timestamp = null)
    {
        return new ReproHostMessageEnvelope
        {
            Type = ReproHostMessageTypes.Progress,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Event = stage,
            Progress = percentComplete,
            Payload = payload is null ? null : JsonSerializer.SerializeToElement(payload, ReproJsonOptions.Default)
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

    public static bool TryParse(string? line, out ReproHostMessageEnvelope? envelope, out JsonException? error)
    {
        envelope = null;
        error = null;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<ReproHostMessageEnvelope>(line, ReproJsonOptions.Default);
            return envelope is not null;
        }
        catch (JsonException jsonException)
        {
            error = jsonException;
            return false;
        }
    }
}
