using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LiteDB.ReproRunner.Shared.Messaging;

/// <summary>
/// Coordinates structured communication between repro processes and the host CLI.
/// </summary>
public sealed class ReproHostClient
{
    private readonly TextWriter _writer;
    private readonly TextReader _reader;
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReproHostClient"/> class.
    /// </summary>
    /// <param name="writer">The writer used to send messages to the host.</param>
    /// <param name="reader">The reader used to receive messages from the host.</param>
    /// <param name="options">Optional serializer options to customize message encoding.</param>
    public ReproHostClient(TextWriter writer, TextReader reader, JsonSerializerOptions? options = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _options = options ?? ReproJsonOptions.Default;
    }

    /// <summary>
    /// Creates a client that communicates over the console standard streams.
    /// </summary>
    /// <returns>The created client instance.</returns>
    public static ReproHostClient CreateDefault()
    {
        return new ReproHostClient(Console.Out, Console.In);
    }

    /// <summary>
    /// Sends a structured envelope to the host asynchronously.
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    public Task SendAsync(ReproHostMessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope is null)
        {
            throw new ArgumentNullException(nameof(envelope));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(envelope, _options);
        return WriteAsync(json, cancellationToken);
    }

    /// <summary>
    /// Sends a structured log message to the host.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="level">The severity associated with the log message.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    public Task SendLogAsync(string message, ReproHostLogLevel level = ReproHostLogLevel.Information, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateLog(message, level), cancellationToken);
    }

    /// <summary>
    /// Sends a structured result message to the host.
    /// </summary>
    /// <param name="success">Indicates whether the repro succeeded.</param>
    /// <param name="summary">An optional summary describing the result.</param>
    /// <param name="payload">Optional payload accompanying the result.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    public Task SendResultAsync(bool success, string? summary = null, object? payload = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateResult(success, summary, payload), cancellationToken);
    }

    /// <summary>
    /// Sends a lifecycle notification to the host.
    /// </summary>
    /// <param name="stage">The lifecycle stage being reported.</param>
    /// <param name="payload">Optional payload accompanying the notification.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    public Task SendLifecycleAsync(string stage, object? payload = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateLifecycle(stage, payload), cancellationToken);
    }

    /// <summary>
    /// Sends a progress update to the host.
    /// </summary>
    /// <param name="stage">The progress stage being reported.</param>
    /// <param name="percentComplete">The optional percentage complete.</param>
    /// <param name="payload">Optional payload accompanying the notification.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    public Task SendProgressAsync(string stage, double? percentComplete = null, object? payload = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateProgress(stage, percentComplete, payload), cancellationToken);
    }

    /// <summary>
    /// Sends a configuration handshake to the host.
    /// </summary>
    /// <param name="useProjectReference">Indicates whether the repro was built against the source project.</param>
    /// <param name="liteDbPackageVersion">The LiteDB package version referenced by the repro, when applicable.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>A task that completes when the message has been written.</returns>
    public Task SendConfigurationAsync(bool useProjectReference, string? liteDbPackageVersion, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateConfiguration(useProjectReference, liteDbPackageVersion), cancellationToken);
    }

    /// <summary>
    /// Sends a structured log message to the host synchronously.
    /// </summary>
    /// <param name="message">The log message text.</param>
    /// <param name="level">The severity associated with the log message.</param>
    public void SendLog(string message, ReproHostLogLevel level = ReproHostLogLevel.Information)
    {
        SendLogAsync(message, level).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends a structured result message to the host synchronously.
    /// </summary>
    /// <param name="success">Indicates whether the repro succeeded.</param>
    /// <param name="summary">An optional summary describing the result.</param>
    /// <param name="payload">Optional payload accompanying the result.</param>
    public void SendResult(bool success, string? summary = null, object? payload = null)
    {
        SendResultAsync(success, summary, payload).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends a lifecycle notification to the host synchronously.
    /// </summary>
    /// <param name="stage">The lifecycle stage being reported.</param>
    /// <param name="payload">Optional payload accompanying the notification.</param>
    public void SendLifecycle(string stage, object? payload = null)
    {
        SendLifecycleAsync(stage, payload).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends a progress update to the host synchronously.
    /// </summary>
    /// <param name="stage">The progress stage being reported.</param>
    /// <param name="percentComplete">The optional percentage complete.</param>
    /// <param name="payload">Optional payload accompanying the notification.</param>
    public void SendProgress(string stage, double? percentComplete = null, object? payload = null)
    {
        SendProgressAsync(stage, percentComplete, payload).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sends a configuration handshake to the host synchronously.
    /// </summary>
    /// <param name="useProjectReference">Indicates whether the repro was built against the source project.</param>
    /// <param name="liteDbPackageVersion">The LiteDB package version referenced by the repro, when applicable.</param>
    public void SendConfiguration(bool useProjectReference, string? liteDbPackageVersion)
    {
        SendConfigurationAsync(useProjectReference, liteDbPackageVersion).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Reads a single input envelope from the host.
    /// </summary>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>The next input envelope, or <c>null</c> when the stream ends.</returns>
    public async Task<ReproInputEnvelope?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await _reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (ReproInputEnvelope.TryParse(line, out var envelope, out _))
            {
                return envelope;
            }
        }
    }

    /// <summary>
    /// Reads input envelopes from the host until the stream is exhausted.
    /// </summary>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>An async stream of input envelopes.</returns>
    public async IAsyncEnumerable<ReproInputEnvelope> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var envelope = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (envelope is null)
            {
                yield break;
            }

            yield return envelope;
        }
    }

    private async Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer.WriteLineAsync(json).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
    }
}
