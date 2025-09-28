using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace LiteDB.ReproRunner.Cli;

internal sealed class ReproExecutor
{
    private readonly TextWriter _standardOut;
    private readonly TextWriter _standardError;
    private readonly object _writeLock = new();

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

    public async Task<ReproExecutionResult> ExecuteAsync(DiscoveredRepro repro, bool useProjectReference, int instances, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (repro.ProjectPath is null)
        {
            return new ReproExecutionResult(useProjectReference, false, 2, TimeSpan.Zero);
        }

        var manifest = repro.Manifest ?? throw new InvalidOperationException("Manifest is required to execute a repro.");
        var projectPath = repro.ProjectPath;
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var stopwatch = Stopwatch.StartNew();

        var buildExitCode = await RunProcessAsync(
            projectDirectory,
            new[]
            {
                "build",
                projectPath,
                "-c", "Release",
                $"-p:UseProjectReference={(useProjectReference ? "true" : "false")}",
                "--nologo"
            },
            cancellationToken).ConfigureAwait(false);

        if (buildExitCode != 0)
        {
            stopwatch.Stop();
            return new ReproExecutionResult(useProjectReference, false, buildExitCode, stopwatch.Elapsed);
        }

        var sharedKey = !string.IsNullOrWhiteSpace(manifest.SharedDatabaseKey) ? manifest.SharedDatabaseKey! : manifest.Id;
        var runIdentifier = Guid.NewGuid().ToString("N");
        var sharedRoot = Path.Combine(Path.GetTempPath(), "LiteDB.ReproRunner", sharedKey, runIdentifier);
        Directory.CreateDirectory(sharedRoot);

        var exitCode = await RunInstancesAsync(
            manifest,
            projectDirectory,
            projectPath,
            useProjectReference,
            instances,
            timeoutSeconds,
            sharedRoot,
            runIdentifier,
            cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        return new ReproExecutionResult(useProjectReference, exitCode == 0, exitCode, stopwatch.Elapsed);
    }

    private async Task<int> RunInstancesAsync(
        ReproManifest manifest,
        string projectDirectory,
        string projectPath,
        bool useProjectReference,
        int instances,
        int timeoutSeconds,
        string sharedRoot,
        string runIdentifier,
        CancellationToken cancellationToken)
    {
        var runArgs = new List<string>
        {
            "run",
            "--project", projectPath,
            "-c", "Release",
            "--no-build",
            $"-p:UseProjectReference={(useProjectReference ? "true" : "false")}",
        };

        if (manifest.Args.Count > 0)
        {
            runArgs.Add("--");
            runArgs.AddRange(manifest.Args);
        }

        var processes = new List<Process>();
        var outputTasks = new List<Task>();
        var errorTasks = new List<Task>();

        try
        {
            for (var index = 0; index < instances; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var startInfo = CreateStartInfo(projectDirectory, runArgs);
                startInfo.Environment["LITEDB_RR_SHARED_DB"] = sharedRoot;
                startInfo.Environment["LITEDB_RR_INSTANCE_INDEX"] = index.ToString();
                startInfo.Environment["LITEDB_RR_TOTAL_INSTANCES"] = instances.ToString();

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

    private static ProcessStartInfo CreateStartInfo(string workingDirectory, IEnumerable<string> arguments)
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

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<int> RunProcessAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(workingDirectory, arguments);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start process.");

        var outputPump = DrainStreamAsync(process.StandardOutput, isError: false, cancellationToken);
        var errorPump = DrainStreamAsync(process.StandardError, isError: true, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);

        return process.ExitCode;
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
        var formatted = $"[{instanceIndex}] {message}";

        switch (envelope.Level)
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

    private async Task SendHostHandshakeAsync(Process process, ReproManifest manifest, string sharedRoot, string runIdentifier, int instanceIndex, int totalInstances, CancellationToken cancellationToken)
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

    private async Task DrainStreamAsync(StreamReader reader, bool isError, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (isError)
                {
                    WriteErrorLine(line);
                }
                else
                {
                    WriteOutputLine(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
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
}

internal readonly record struct ReproExecutionResult(bool UseProjectReference, bool Reproduced, int ExitCode, TimeSpan Duration);
