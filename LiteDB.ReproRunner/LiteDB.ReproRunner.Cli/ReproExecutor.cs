using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace LiteDB.ReproRunner.Cli;

internal sealed class ReproExecutor
{
    private readonly IConsole _console;

    public ReproExecutor(IConsole console)
    {
        _console = console;
    }

    public async Task<int> ExecuteAsync(DiscoveredRepro repro, ReproExecutionOptions options, CancellationToken cancellationToken)
    {
        if (repro.ProjectPath is null)
        {
            _console.Error.WriteLine($"Unable to locate project file for '{repro.Manifest?.Id ?? repro.RawId}'.");
            return 2;
        }

        var projectPath = repro.ProjectPath;
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var manifest = repro.Manifest ?? throw new InvalidOperationException("Manifest is required to execute a repro.");

        _console.Out.WriteLine($"Building {Path.GetFileName(projectPath)} (UseProjectReference={options.UseProjectReference.ToString().ToLowerInvariant()})...");

        var buildArgs = new List<string>
        {
            "build",
            projectPath,
            "-c", "Release",
            $"-p:UseProjectReference={(options.UseProjectReference ? "true" : "false")}",
            "--nologo"
        };

        var buildExitCode = await RunProcessAsync(projectDirectory, buildArgs, cancellationToken).ConfigureAwait(false);
        if (buildExitCode != 0)
        {
            _console.Error.WriteLine($"dotnet build returned exit code {buildExitCode}.");
            return buildExitCode;
        }

        var sharedKey = !string.IsNullOrWhiteSpace(manifest.SharedDatabaseKey) ? manifest.SharedDatabaseKey! : manifest.Id;
        var runIdentifier = Guid.NewGuid().ToString("N");
        var sharedRoot = Path.Combine(Path.GetTempPath(), "LiteDB.ReproRunner", sharedKey, runIdentifier);
        Directory.CreateDirectory(sharedRoot);

        _console.Out.WriteLine($"Running {manifest.Id} with {options.Instances} instance(s), timeout {options.TimeoutSeconds}s.");

        var runArgs = new List<string>
        {
            "run",
            "--project", projectPath,
            "-c", "Release",
            "--no-build",
            $"-p:UseProjectReference={(options.UseProjectReference ? "true" : "false")}" 
        };

        if (manifest.Args.Count > 0)
        {
            runArgs.Add("--");
            foreach (var arg in manifest.Args)
            {
                runArgs.Add(arg);
            }
        }

        var processes = new List<Process>();

        try
        {
            for (var index = 0; index < options.Instances; index++)
            {
                var startInfo = CreateStartInfo(projectDirectory, runArgs);
                startInfo.Environment["LITEDB_RR_SHARED_DB"] = sharedRoot;
                startInfo.Environment["LITEDB_RR_INSTANCE_INDEX"] = index.ToString();
                startInfo.Environment["LITEDB_RR_TOTAL_INSTANCES"] = options.Instances.ToString();

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    throw new InvalidOperationException("Failed to start repro process.");
                }

                processes.Add(process);
            }

            var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            var waitTasks = processes.Select(p => p.WaitForExitAsync(cancellationToken)).ToArray();
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(Task.WhenAll(waitTasks), timeoutTask).ConfigureAwait(false);

            if (completed == timeoutTask)
            {
                _console.Error.WriteLine($"Timed out after {timeout.TotalSeconds} seconds. Terminating instances...");
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
                    _console.Error.WriteLine($"Instance {index} exited with code {process.ExitCode}.");
                    exitCode = exitCode == 0 ? process.ExitCode : exitCode;
                }
            }

            if (exitCode == 0)
            {
                _console.Out.WriteLine("All instances completed successfully.");
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
            UseShellExecute = false
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
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
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

internal sealed class ReproExecutionOptions
{
    public bool UseProjectReference { get; set; }

    public int Instances { get; set; }

    public int TimeoutSeconds { get; set; }
}
