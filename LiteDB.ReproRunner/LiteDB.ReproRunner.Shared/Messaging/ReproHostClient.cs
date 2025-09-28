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

    public ReproHostClient(TextWriter writer, TextReader reader, JsonSerializerOptions? options = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _options = options ?? ReproJsonOptions.Default;
    }

    public static ReproHostClient CreateDefault()
    {
        return new ReproHostClient(Console.Out, Console.In);
    }

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

    public Task SendLogAsync(string message, ReproHostLogLevel level = ReproHostLogLevel.Information, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateLog(message, level), cancellationToken);
    }

    public Task SendResultAsync(bool success, string? summary = null, object? payload = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateResult(success, summary, payload), cancellationToken);
    }

    public Task SendLifecycleAsync(string stage, object? payload = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateLifecycle(stage, payload), cancellationToken);
    }

    public Task SendProgressAsync(string stage, double? percentComplete = null, object? payload = null, CancellationToken cancellationToken = default)
    {
        return SendAsync(ReproHostMessageEnvelope.CreateProgress(stage, percentComplete, payload), cancellationToken);
    }

    public void SendLog(string message, ReproHostLogLevel level = ReproHostLogLevel.Information)
    {
        SendLogAsync(message, level).GetAwaiter().GetResult();
    }

    public void SendResult(bool success, string? summary = null, object? payload = null)
    {
        SendResultAsync(success, summary, payload).GetAwaiter().GetResult();
    }

    public void SendLifecycle(string stage, object? payload = null)
    {
        SendLifecycleAsync(stage, payload).GetAwaiter().GetResult();
    }

    public void SendProgress(string stage, double? percentComplete = null, object? payload = null)
    {
        SendProgressAsync(stage, percentComplete, payload).GetAwaiter().GetResult();
    }

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
