using System.Diagnostics;

internal static class ChildProbeRunner
{
    public const int ExecutionTimeoutSeconds = 5;

    private const int TerminationGraceMilliseconds = 1_000;
    private const int OutputDrainGraceMilliseconds = 1_000;

    public static ChildProbeResult Run(string assemblyPath, string mode, string databasePath)
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

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("could not start child probe");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        var exited = process.WaitForExit(ExecutionTimeoutSeconds * 1_000) || HasExited(process);
        if (exited)
        {
            var outputComplete = WaitForOutput(stdout, stderr);
            return CreateResult(process, false, true, outputComplete, stdout, stderr, "completed");
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
            true,
            terminated,
            drained,
            stdout,
            stderr,
            $"kill={shutdown}, exited={terminated}, outputDrained={drained}");
    }

    private static ChildProbeResult CreateResult(
        Process process,
        bool timedOut,
        bool terminationConfirmed,
        bool outputComplete,
        Task<string> stdout,
        Task<string> stderr,
        string shutdownDetail)
    {
        int? exitCode = HasExited(process) ? process.ExitCode : null;
        return new ChildProbeResult(
            timedOut,
            terminationConfirmed,
            exitCode,
            outputComplete,
            GetCapturedOutput(stdout),
            GetCapturedOutput(stderr),
            shutdownDetail);
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
        catch (Exception failure)
        {
            return "failed-" + failure.GetType().Name;
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
            // The status checks below distinguish complete output from a failed read.
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
            // Stream closure is best effort after both bounded waits have elapsed.
        }
    }
}

internal sealed record ChildProbeResult(
    bool TimedOut,
    bool TerminationConfirmed,
    int? ExitCode,
    bool OutputComplete,
    string StandardOutput,
    string StandardError,
    string ShutdownDetail)
{
    public string Describe(string operation = "control") =>
        $"{operation} timeout={TimedOut}, exit={ExitCode?.ToString() ?? "still-running"}, " +
        $"terminated={TerminationConfirmed}, outputComplete={OutputComplete}, shutdown={ShutdownDetail}, " +
        $"stdout={StandardOutput}, stderr={StandardError}";
}
