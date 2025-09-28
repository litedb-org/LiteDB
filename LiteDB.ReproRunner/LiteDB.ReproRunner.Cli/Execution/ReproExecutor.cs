using System.Diagnostics;
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
    private readonly TextWriter _standardOut;
    private readonly TextWriter _standardError;
    private readonly object _writeLock = new();

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
            return new ReproExecutionResult(build.Plan.UseProjectReference, false, build.ExitCode, TimeSpan.Zero);
        }

        var repro = build.Plan.Repro;

        if (repro.ProjectPath is null)
        {
            return new ReproExecutionResult(build.Plan.UseProjectReference, false, build.ExitCode, TimeSpan.Zero);
        }

        var manifest = repro.Manifest ?? throw new InvalidOperationException("Manifest is required to execute a repro.");
        var projectDirectory = Path.GetDirectoryName(repro.ProjectPath)!;
        var stopwatch = Stopwatch.StartNew();

        var sharedKey = !string.IsNullOrWhiteSpace(manifest.SharedDatabaseKey)
            ? manifest.SharedDatabaseKey!
            : manifest.Id;

        var runIdentifier = Guid.NewGuid().ToString("N");
        var sharedRoot = Path.Combine(build.Plan.ExecutionRootDirectory, Sanitize(sharedKey), runIdentifier);
        Directory.CreateDirectory(sharedRoot);

        var exitCode = await RunInstancesAsync(
            manifest,
            projectDirectory,
            build.AssemblyPath,
            instances,
            timeoutSeconds,
            sharedRoot,
            runIdentifier,
            cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        return new ReproExecutionResult(build.Plan.UseProjectReference, exitCode == 0, exitCode, stopwatch.Elapsed);
    }

    private async Task<int> RunInstancesAsync(
        ReproManifest manifest,
        string projectDirectory,
        string assemblyPath,
        int instances,
        int timeoutSeconds,
        string sharedRoot,
        string runIdentifier,
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
                outputTasks.Add(PumpStandardOutputAsync(process, index, cancellationToken));
                errorTasks.Add(PumpStandardErrorAsync(process, index, cancellationToken));

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

    private async Task PumpStandardOutputAsync(Process process, int instanceIndex, CancellationToken cancellationToken)
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

    private async Task PumpStandardErrorAsync(Process process, int instanceIndex, CancellationToken cancellationToken)
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

        HandleStructuredMessage(instanceIndex, envelope!);
        return true;
    }

    private void HandleStructuredMessage(int instanceIndex, ReproHostMessageEnvelope envelope)
    {
        StructuredMessageObserver?.Invoke(instanceIndex, envelope);

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
            default:
                WriteOutputLine($"[{instanceIndex}] {envelope.Type}: {envelope.Text ?? string.Empty}");
                break;
        }
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

    private void WriteOutputLine(string message)
    {
        lock (_writeLock)
        {
            _standardOut.WriteLine(message);
            _standardOut.Flush();
        }
    }

    private void WriteErrorLine(string message)
    {
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
}

/// <summary>
/// Represents the result of executing a repro variant.
/// </summary>
/// <param name="UseProjectReference">Indicates whether the run targeted the source project build.</param>
/// <param name="Reproduced">Indicates whether the repro successfully reproduced the issue.</param>
/// <param name="ExitCode">The exit code reported by the repro host.</param>
/// <param name="Duration">The elapsed time for the execution.</param>
internal readonly record struct ReproExecutionResult(bool UseProjectReference, bool Reproduced, int ExitCode, TimeSpan Duration);

/// <summary>
/// Represents a structured log entry emitted during repro execution.
/// </summary>
/// <param name="InstanceIndex">The zero-based instance index originating the log entry.</param>
/// <param name="Message">The log message text.</param>
/// <param name="Level">The severity associated with the log entry.</param>
internal readonly record struct ReproExecutionLogEntry(int InstanceIndex, string Message, ReproHostLogLevel Level);
