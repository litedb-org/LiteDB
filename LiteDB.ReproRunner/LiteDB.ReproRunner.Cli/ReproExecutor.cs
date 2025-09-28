using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LiteDB.ReproRunner.Cli;

internal sealed class ReproExecutor
{
    public ReproExecutor()
    {
    }

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
            }

            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            var waitTasks = processes.Select(p => p.WaitForExitAsync(cancellationToken)).ToList();
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(Task.WhenAll(waitTasks), timeoutTask).ConfigureAwait(false);

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

            var exitCode = 0;

            for (var index = 0; index < processes.Count; index++)
            {
                var process = processes[index];
                if (process.ExitCode != 0)
                {
                    exitCode = exitCode == 0 ? process.ExitCode : exitCode;
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

        var outputPump = DrainStreamAsync(process.StandardOutput, cancellationToken);
        var errorPump = DrainStreamAsync(process.StandardError, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);

        return process.ExitCode;
    }

    private static async Task DrainStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
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
