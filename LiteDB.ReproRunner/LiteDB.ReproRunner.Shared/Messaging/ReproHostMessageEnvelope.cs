using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Represents a structured message emitted by a repro process.
/// </summary>
public sealed class ReproHostMessageEnvelope
{
    /// <summary>
    /// Gets or sets the message type identifier.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Gets or sets the timestamp associated with the message.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets the log level for log messages.
    /// </summary>
    [JsonPropertyName("level")]
    public ReproHostLogLevel? Level { get; init; }

    /// <summary>
    /// Gets or sets the textual payload associated with the message.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Gets or sets the success flag for result messages.
    /// </summary>
    [JsonPropertyName("success")]
    public bool? Success { get; init; }

    /// <summary>
    /// Gets or sets the lifecycle or progress event name.
    /// </summary>
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    /// <summary>
    /// Gets or sets the percentage complete for progress messages.
    /// </summary>
    [JsonPropertyName("progress")]
    public double? Progress { get; init; }

    /// <summary>
    /// Gets or sets the optional payload serialized with the message.
    /// </summary>
    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; init; }

    /// <summary>
    /// Creates a log message envelope.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="level">The severity associated with the log message.</param>
    /// <param name="timestamp">An optional timestamp to associate with the message.</param>
    /// <returns>The constructed log message envelope.</returns>
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

    /// <summary>
    /// Creates a result message envelope.
    /// </summary>
    /// <param name="success">Indicates whether the repro succeeded.</param>
    /// <param name="summary">An optional summary describing the result.</param>
    /// <param name="payload">Optional payload associated with the result.</param>
    /// <param name="timestamp">An optional timestamp to associate with the message.</param>
    /// <returns>The constructed result message envelope.</returns>
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

    /// <summary>
    /// Creates a lifecycle message envelope.
    /// </summary>
    /// <param name="stage">The lifecycle stage being reported.</param>
    /// <param name="payload">Optional payload associated with the lifecycle event.</param>
    /// <param name="timestamp">An optional timestamp to associate with the message.</param>
    /// <returns>The constructed lifecycle message envelope.</returns>
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

    /// <summary>
    /// Creates a progress message envelope.
    /// </summary>
    /// <param name="stage">The progress stage being reported.</param>
    /// <param name="percentComplete">The optional percentage complete.</param>
    /// <param name="payload">Optional payload associated with the progress update.</param>
    /// <param name="timestamp">An optional timestamp to associate with the message.</param>
    /// <returns>The constructed progress message envelope.</returns>
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
    /// Attempts to parse a message envelope from its JSON representation.
    /// </summary>
    /// <param name="line">The JSON text to parse.</param>
    /// <param name="envelope">When this method returns, contains the parsed envelope if successful.</param>
    /// <param name="error">When this method returns, contains the parsing error if parsing failed.</param>
    /// <returns><c>true</c> if the envelope was parsed successfully; otherwise, <c>false</c>.</returns>
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
