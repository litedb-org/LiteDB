using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using LiteDB.ReproRunner.Cli.Manifests;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace LiteDB.ReproRunner.Cli.Execution;

/// <summary>
/// Executes built repro assemblies and relays their structured output.
/// </summary>
internal sealed class ReproExecutor
{
    private const int CapturedOutputLimit = 200;

    private readonly TextWriter _standardOut;
    private readonly TextWriter _standardError;
    private readonly object _writeLock = new();
    private readonly object _configurationLock = new();
    private readonly Dictionary<int, ConfigurationState> _configurationStates = new();
    private ConfigurationExpectation? _configurationExpectation;
    private bool _configurationMismatchDetected;
    private int _expectedConfigurationInstances;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReproExecutor"/> class using the console streams.
    /// </summary>
    public ReproExecutor()
        : this(Console.Out, Console.Error)
    {
    }

    internal ReproExecutor(TextWriter? standardOut, TextWriter? standardError)
    {
        _standardOut = standardOut ?? Console.Out;
        _standardError = standardError ?? Console.Error;
    }

    internal Action<int, ReproHostMessageEnvelope>? StructuredMessageObserver { get; set; }

    internal Action<ReproExecutionLogEntry>? LogObserver { get; set; }

    internal bool SuppressConsoleLogOutput { get; set; }

    internal void ConfigureExpectedConfiguration(bool useProjectReference, string? liteDbPackageVersion, int instanceCount)
    {
        var normalizedVersion = string.IsNullOrWhiteSpace(liteDbPackageVersion)
            ? null
            : liteDbPackageVersion.Trim();

        lock (_configurationLock)
        {
            _configurationExpectation = new ConfigurationExpectation(useProjectReference, normalizedVersion);
            _configurationStates.Clear();
            _expectedConfigurationInstances = Math.Max(instanceCount, 0);
            _configurationMismatchDetected = false;

            for (var index = 0; index < _expectedConfigurationInstances; index++)
            {
                _configurationStates[index] = new ConfigurationState();
            }
        }
    }

    /// <summary>
    /// Executes the provided repro build across the requested number of instances.
    /// </summary>
    /// <param name="build">The build to execute.</param>
    /// <param name="instances">The number of instances to launch.</param>
    /// <param name="timeoutSeconds">The timeout applied to the execution.</param>
    /// <param name="cancellationToken">The token used to observe cancellation requests.</param>
    /// <returns>The execution result for the run.</returns>
    public async Task<ReproExecutionResult> ExecuteAsync(
        ReproBuildResult build,
        int instances,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (build is null)
        {
            throw new ArgumentNullException(nameof(build));
        }

        if (!build.Succeeded || string.IsNullOrWhiteSpace(build.AssemblyPath))
        {
            return new ReproExecutionResult(build.Plan.UseProjectReference, false, build.ExitCode, TimeSpan.Zero, Array.Empty<ReproExecutionCapturedLine>());
        }

        var repro = build.Plan.Repro;

        if (repro.ProjectPath is null)
        {
            return new ReproExecutionResult(build.Plan.UseProjectReference, false, build.ExitCode, TimeSpan.Zero, Array.Empty<ReproExecutionCapturedLine>());
        }

        var manifest = repro.Manifest ?? throw new InvalidOperationException("Manifest is required to execute a repro.");
        ConfigureExpectedConfiguration(build.Plan.UseProjectReference, build.Plan.LiteDBPackageVersion, instances);
        var projectDirectory = Path.GetDirectoryName(repro.ProjectPath)!;
        var stopwatch = Stopwatch.StartNew();

        var sharedKey = !string.IsNullOrWhiteSpace(manifest.SharedDatabaseKey)
            ? manifest.SharedDatabaseKey!
            : manifest.Id;

        var runIdentifier = Guid.NewGuid().ToString("N");
        var sharedRoot = Path.Combine(build.Plan.ExecutionRootDirectory, Sanitize(sharedKey), runIdentifier);
        Directory.CreateDirectory(sharedRoot);

        var capturedOutput = new BoundedLogBuffer(CapturedOutputLimit);

        try
        {
            var exitCode = await RunInstancesAsync(
                manifest,
                projectDirectory,
                build.AssemblyPath,
                instances,
                timeoutSeconds,
                sharedRoot,
                runIdentifier,
                capturedOutput,
                cancellationToken).ConfigureAwait(false);

            FinalizeConfigurationValidation();
            var configurationMismatch = HasConfigurationMismatch();

            if (configurationMismatch && exitCode == 0)
            {
                exitCode = -2;
            }

            stopwatch.Stop();
            return new ReproExecutionResult(
                build.Plan.UseProjectReference,
                exitCode == 0 && !configurationMismatch,
                exitCode,
                stopwatch.Elapsed,
                capturedOutput.ToSnapshot());
        }
        finally
        {
            ResetConfigurationExpectation();
        }
    }

    private async Task<int> RunInstancesAsync(
        ReproManifest manifest,
        string projectDirectory,
        string assemblyPath,
        int instances,
        int timeoutSeconds,
        string sharedRoot,
        string runIdentifier,
        BoundedLogBuffer capturedOutput,
        CancellationToken cancellationToken)
    {
        var manifestArgs = manifest.Args;
        var processes = new List<Process>();
        var outputTasks = new List<Task>();
        var errorTasks = new List<Task>();

        try
        {
            for (var index = 0; index < instances; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startInfo = CreateStartInfo(projectDirectory, assemblyPath, manifestArgs);
                startInfo.Environment["LITEDB_RR_SHARED_DB"] = sharedRoot;
                startInfo.Environment["LITEDB_RR_INSTANCE_INDEX"] = index.ToString();
                startInfo.Environment["LITEDB_RR_TOTAL_INSTANCES"] = instances.ToString();
                startInfo.Environment["LITEDB_RR_RUN_IDENTIFIER"] = runIdentifier;

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    throw new InvalidOperationException("Failed to start repro process.");
                }

                processes.Add(process);
                outputTasks.Add(PumpStandardOutputAsync(process, index, capturedOutput, cancellationToken));
                errorTasks.Add(PumpStandardErrorAsync(process, index, capturedOutput, cancellationToken));

                await SendHostHandshakeAsync(process, manifest, sharedRoot, runIdentifier, index, instances, cancellationToken).ConfigureAwait(false);
            }

            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            var waitTasks = processes.Select(p => p.WaitForExitAsync(cancellationToken)).ToList();
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var allProcessesTask = Task.WhenAll(waitTasks);
            var completed = await Task.WhenAny(allProcessesTask, timeoutTask).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (completed == timeoutTask)
            {
                foreach (var process in processes)
                {
                    TryKill(process);
                }

                return 1;
            }

            await allProcessesTask.ConfigureAwait(false);
            await Task.WhenAll(outputTasks.Concat(errorTasks)).ConfigureAwait(false);

            var exitCode = 0;

            foreach (var process in processes)
            {
                if (process.ExitCode != 0 && exitCode == 0)
                {
                    exitCode = process.ExitCode;
                }
            }

            return exitCode;
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited)
                {
                    TryKill(process);
                }

                process.Dispose();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, string assemblyPath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(assemblyPath);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task PumpStandardOutputAsync(Process process, int instanceIndex, BoundedLogBuffer capturedOutput, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                capturedOutput.Add(ReproExecutionStream.StandardOutput, line);
                if (!TryProcessStructuredLine(line, instanceIndex))
                {
                    WriteOutputLine($"[{instanceIndex}] {line}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task PumpStandardErrorAsync(Process process, int instanceIndex, BoundedLogBuffer capturedOutput, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                capturedOutput.Add(ReproExecutionStream.StandardError, line);
                WriteErrorLine($"[{instanceIndex}] {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    internal bool TryProcessStructuredLine(string line, int instanceIndex)
    {
        if (!ReproHostMessageEnvelope.TryParse(line, out var envelope, out _))
        {
            return false;
        }

        StructuredMessageObserver?.Invoke(instanceIndex, envelope!);

        if (!HandleConfigurationHandshake(instanceIndex, envelope!))
        {
            return true;
        }

        HandleStructuredMessage(instanceIndex, envelope!);
        return true;
    }

    private void HandleStructuredMessage(int instanceIndex, ReproHostMessageEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case ReproHostMessageTypes.Log:
                WriteLogMessage(instanceIndex, envelope);
                break;
            case ReproHostMessageTypes.Result:
                WriteResultMessage(instanceIndex, envelope);
                break;
            case ReproHostMessageTypes.Lifecycle:
                WriteOutputLine($"[{instanceIndex}] lifecycle: {envelope.Event ?? "(unknown)"}");
                break;
            case ReproHostMessageTypes.Progress:
                var suffix = envelope.Progress is double progress
                    ? $" ({progress:0.##}%)"
                    : string.Empty;
                WriteOutputLine($"[{instanceIndex}] progress: {envelope.Event ?? "(unknown)"}{suffix}");
                break;
            case ReproHostMessageTypes.Configuration:
                break;
            default:
                WriteOutputLine($"[{instanceIndex}] {envelope.Type}: {envelope.Text ?? string.Empty}");
                break;
        }
    }

    private bool HandleConfigurationHandshake(int instanceIndex, ReproHostMessageEnvelope envelope)
    {
        string? errorMessage = null;
        var shouldProcess = true;

        lock (_configurationLock)
        {
            if (_configurationExpectation is not { } expectation)
            {
                if (string.Equals(envelope.Type, ReproHostMessageTypes.Configuration, StringComparison.Ordinal))
                {
                    shouldProcess = false;
                }

                return shouldProcess;
            }

            if (!_configurationStates.TryGetValue(instanceIndex, out var state))
            {
                state = new ConfigurationState();
                _configurationStates[instanceIndex] = state;
            }

            if (!state.Received)
            {
                if (!string.Equals(envelope.Type, ReproHostMessageTypes.Configuration, StringComparison.Ordinal))
                {
                    errorMessage = "expected configuration handshake before other messages.";
                    state.Received = true;
                    state.IsValid = false;
                    _configurationMismatchDetected = true;
                    shouldProcess = false;
                }
                else
                {
                    var payload = envelope.DeserializePayload<ReproHostConfigurationPayload>();
                    if (payload is null)
                    {
                        errorMessage = "reported configuration without a payload.";
                        state.Received = true;
                        state.IsValid = false;
                        _configurationMismatchDetected = true;
                        shouldProcess = false;
                    }
                    else
                    {
                        var actualVersion = string.IsNullOrWhiteSpace(payload.LiteDBPackageVersion)
                            ? null
                            : payload.LiteDBPackageVersion.Trim();

                        var expectedVersion = expectation.LiteDbPackageVersion;
                        var versionMatches = string.Equals(
                            actualVersion ?? string.Empty,
                            expectedVersion ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase);

                        if (payload.UseProjectReference != expectation.UseProjectReference || !versionMatches)
                        {
                            var expectedVersionDisplay = expectedVersion ?? "(unspecified)";
                            var actualVersionDisplay = actualVersion ?? "(unspecified)";
                            errorMessage = $"reported configuration UseProjectReference={payload.UseProjectReference}, LiteDBPackageVersion={actualVersionDisplay} but expected UseProjectReference={expectation.UseProjectReference}, LiteDBPackageVersion={expectedVersionDisplay}.";
                            state.IsValid = false;
                            _configurationMismatchDetected = true;
                        }
                        else
                        {
                            state.IsValid = true;
                        }

                        state.Received = true;
                        shouldProcess = false;
                    }
                }
            }
            else if (!state.IsValid)
            {
                shouldProcess = false;
            }
            else if (string.Equals(envelope.Type, ReproHostMessageTypes.Configuration, StringComparison.Ordinal))
            {
                shouldProcess = false;
            }
        }

        if (errorMessage is not null)
        {
            WriteConfigurationError(instanceIndex, errorMessage);
        }

        return shouldProcess;
    }

    private void FinalizeConfigurationValidation()
    {
        List<int>? missingInstances = null;

        lock (_configurationLock)
        {
            if (_configurationExpectation is null)
            {
                return;
            }

            for (var index = 0; index < _expectedConfigurationInstances; index++)
            {
                if (!_configurationStates.TryGetValue(index, out var state))
                {
                    state = new ConfigurationState();
                    _configurationStates[index] = state;
                }

                if (!state.Received)
                {
                    state.Received = true;
                    state.IsValid = false;
                    _configurationMismatchDetected = true;
                    missingInstances ??= new List<int>();
                    missingInstances.Add(index);
                }
            }
        }

        if (missingInstances is null)
        {
            return;
        }

        foreach (var instanceIndex in missingInstances)
        {
            WriteConfigurationError(instanceIndex, "did not report configuration handshake.");
        }
    }

    private bool HasConfigurationMismatch()
    {
        lock (_configurationLock)
        {
            if (_configurationExpectation is null)
            {
                return false;
            }

            if (_configurationMismatchDetected)
            {
                return true;
            }

            foreach (var state in _configurationStates.Values)
            {
                if (!state.IsValid)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private void ResetConfigurationExpectation()
    {
        lock (_configurationLock)
        {
            _configurationExpectation = null;
            _configurationStates.Clear();
            _configurationMismatchDetected = false;
            _expectedConfigurationInstances = 0;
        }
    }

    private void WriteConfigurationError(int instanceIndex, string message)
    {
        LogObserver?.Invoke(new ReproExecutionLogEntry(instanceIndex, $"configuration error: {message}", ReproHostLogLevel.Error));

        if (SuppressConsoleLogOutput)
        {
            return;
        }

        WriteErrorLine($"[{instanceIndex}] configuration error: {message}");
    }

    private void WriteLogMessage(int instanceIndex, ReproHostMessageEnvelope envelope)
    {
        var message = envelope.Text ?? string.Empty;
        var level = envelope.Level ?? ReproHostLogLevel.Information;
        var formatted = $"[{instanceIndex}] {message}";

        LogObserver?.Invoke(new ReproExecutionLogEntry(instanceIndex, message, level));

        if (SuppressConsoleLogOutput)
        {
            return;
        }

        switch (level)
        {
            case ReproHostLogLevel.Error:
            case ReproHostLogLevel.Critical:
                WriteErrorLine(formatted);
                break;
            case ReproHostLogLevel.Warning:
                WriteErrorLine(formatted);
                break;
            default:
                WriteOutputLine(formatted);
                break;
        }
    }

    private void WriteResultMessage(int instanceIndex, ReproHostMessageEnvelope envelope)
    {
        var success = envelope.Success is true;
        var status = success ? "succeeded" : "completed";
        var summary = envelope.Text ?? $"Repro {status}.";
        WriteOutputLine($"[{instanceIndex}] {summary}");
    }

    private async Task SendHostHandshakeAsync(
        Process process,
        ReproManifest manifest,
        string sharedRoot,
        string runIdentifier,
        int instanceIndex,
        int totalInstances,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = ReproInputEnvelope.CreateHostReady(runIdentifier, sharedRoot, instanceIndex, totalInstances, manifest.Id);
            var json = JsonSerializer.Serialize(envelope, ReproJsonOptions.Default);
            var writer = process.StandardInput;
            writer.AutoFlush = true;
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(json).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private sealed class BoundedLogBuffer
    {
        private readonly int _capacity;
        private readonly Queue<ReproExecutionCapturedLine> _buffer;
        private readonly object _sync = new();

        public BoundedLogBuffer(int capacity)
        {
            _capacity = Math.Max(1, capacity);
            _buffer = new Queue<ReproExecutionCapturedLine>(_capacity);
        }

        public void Add(ReproExecutionStream stream, string text)
        {
            if (text is null)
            {
                return;
            }

            var entry = new ReproExecutionCapturedLine(stream, text);

            lock (_sync)
            {
                _buffer.Enqueue(entry);
                while (_buffer.Count > _capacity)
                {
                    _buffer.Dequeue();
                }
            }
        }

        public IReadOnlyList<ReproExecutionCapturedLine> ToSnapshot()
        {
            lock (_sync)
            {
                return _buffer.ToArray();
            }
        }
    }

    private void WriteOutputLine(string message)
    {
        if (SuppressConsoleLogOutput)
        {
            return;
        }

        lock (_writeLock)
        {
            _standardOut.WriteLine(message);
            _standardOut.Flush();
        }
    }

    private void WriteErrorLine(string message)
    {
        if (SuppressConsoleLogOutput)
        {
            return;
        }

        lock (_writeLock)
        {
            _standardError.WriteLine(message);
            _standardError.Flush();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0)
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? "shared" : builder.ToString();
    }

    private sealed class ConfigurationState
    {
        public bool Received { get; set; }

        public bool IsValid { get; set; } = true;
    }

    private readonly record struct ConfigurationExpectation(bool UseProjectReference, string? LiteDbPackageVersion);
}

/// <summary>
/// Represents the result of executing a repro variant.
/// </summary>
/// <param name="UseProjectReference">Indicates whether the run targeted the source project build.</param>
/// <param name="Reproduced">Indicates whether the repro successfully reproduced the issue.</param>
/// <param name="ExitCode">The exit code reported by the repro host.</param>
/// <param name="Duration">The elapsed time for the execution.</param>
/// <param name="CapturedOutput">The captured standard output and error lines.</param>
internal readonly record struct ReproExecutionResult(bool UseProjectReference, bool Reproduced, int ExitCode, TimeSpan Duration, IReadOnlyList<ReproExecutionCapturedLine> CapturedOutput);

/// <summary>
/// Represents a structured log entry emitted during repro execution.
/// </summary>
/// <param name="InstanceIndex">The zero-based instance index originating the log entry.</param>
/// <param name="Message">The log message text.</param>
/// <param name="Level">The severity associated with the log entry.</param>
internal readonly record struct ReproExecutionLogEntry(int InstanceIndex, string Message, ReproHostLogLevel Level);

/// <summary>
/// Identifies the stream that produced a captured line of output.
/// </summary>
internal enum ReproExecutionStream
{
    StandardOutput,
    StandardError
}

/// <summary>
/// Represents a captured line of standard output or error for report generation.
/// </summary>
/// <param name="Stream">The source stream for the line.</param>
/// <param name="Text">The raw text captured from the process.</param>
internal readonly record struct ReproExecutionCapturedLine(ReproExecutionStream Stream, string Text);
