using System.Diagnostics;

internal static class BoundedUpgradeProbe
{
    public const int ExecutionTimeoutSeconds = 5;
    public const long WorkingSetLimitBytes = 256L * 1024 * 1024;

    private const string ManagedHeapLimitHex = "08000000";
    private const int PollMilliseconds = 10;
    private const int TerminationGraceMilliseconds = 1_000;
    private const int OutputDrainGraceMilliseconds = 1_000;

    public static UpgradeProbeResult Run(string assemblyPath, string mode, string databasePath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(databasePath)!
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(databasePath);
        start.Environment["DOTNET_GCHeapHardLimit"] = ManagedHeapLimitHex;

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("could not start bounded upgrade child");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var stopwatch = Stopwatch.StartNew();
        var peakWorkingSet = 0L;
        ProbeStopReason? stopReason = null;

        while (!HasExited(process))
        {
            peakWorkingSet = Math.Max(peakWorkingSet, ReadWorkingSet(process));
            if (peakWorkingSet > WorkingSetLimitBytes)
            {
                stopReason = ProbeStopReason.MemoryLimit;
                break;
            }
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(ExecutionTimeoutSeconds))
            {
                stopReason = ProbeStopReason.Timeout;
                break;
            }

            Thread.Sleep(PollMilliseconds);
        }

        peakWorkingSet = Math.Max(peakWorkingSet, ReadWorkingSet(process));
        if (stopReason == null)
        {
            var complete = WaitForOutput(stdout, stderr);
            return CreateResult(
                process,
                null,
                complete,
                terminated: true,
                peakWorkingSet,
                stdout,
                stderr,
                "completed");
        }

        var shutdown = RequestTermination(process);
        var terminated = HasExited(process) || process.WaitForExit(TerminationGraceMilliseconds);
        var drained = WaitForOutput(stdout, stderr);
        if (!drained)
        {
            CloseRedirectedStreams(process);
        }

        return CreateResult(
            process,
            stopReason,
            drained,
            terminated,
            peakWorkingSet,
            stdout,
            stderr,
            $"kill={shutdown}, exited={terminated}, outputDrained={drained}");
    }

    private static UpgradeProbeResult CreateResult(
        Process process,
        ProbeStopReason? stopReason,
        bool outputComplete,
        bool terminated,
        long peakWorkingSet,
        Task<string> stdout,
        Task<string> stderr,
        string shutdownDetail)
    {
        int? exitCode = HasExited(process) ? process.ExitCode : null;
        return new UpgradeProbeResult(
            stopReason,
            exitCode,
            outputComplete,
            terminated,
            peakWorkingSet,
            GetCapturedOutput(stdout),
            GetCapturedOutput(stderr),
            shutdownDetail);
    }

    private static long ReadWorkingSet(Process process)
    {
        try
        {
            process.Refresh();
            return process.HasExited ? 0 : process.WorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static string RequestTermination(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            return "requested";
        }
        catch (InvalidOperationException) when (HasExited(process))
        {
            return "already-exited";
        }
        catch (Exception exception)
        {
            return "failed-" + exception.GetType().Name;
        }
    }

    private static bool WaitForOutput(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            Task.WaitAll(new Task[] { stdout, stderr }, OutputDrainGraceMilliseconds);
        }
        catch (AggregateException)
        {
        }

        return stdout.IsCompletedSuccessfully && stderr.IsCompletedSuccessfully;
    }

    private static string GetCapturedOutput(Task<string> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return task.Result;
        }

        _ = task.Exception;
        return task.IsFaulted ? "<output capture failed>" : "<output capture incomplete>";
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void CloseRedirectedStreams(Process process)
    {
        try
        {
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
        catch
        {
        }
    }
}

internal enum ProbeStopReason
{
    Timeout,
    MemoryLimit
}

internal sealed record UpgradeProbeResult(
    ProbeStopReason? StopReason,
    int? ExitCode,
    bool OutputComplete,
    bool Terminated,
    long PeakWorkingSetBytes,
    string StandardOutput,
    string StandardError,
    string ShutdownDetail)
{
    public bool Started => StandardOutput.Contains(UpgradeScenario.StartedMarker, StringComparison.Ordinal);

    public string Describe(string mode) =>
        $"{mode} stop={StopReason?.ToString() ?? "none"}, exit={ExitCode?.ToString() ?? "still-running"}, " +
        $"outputComplete={OutputComplete}, terminated={Terminated}, " +
        $"peakMiB={PeakWorkingSetBytes / 1024d / 1024d:F1}, " +
        $"shutdown={ShutdownDetail}, stdout={StandardOutput}, stderr={StandardError}";
}
