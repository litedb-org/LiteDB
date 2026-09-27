using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LiteDB.Tests.Engine;

/// <summary>Optional test-only evidence collection after the worker deadline has failed.</summary>
internal sealed class SharedWorkerProcessDump
{
    private readonly string _executable;
    private readonly string _directory;
    private readonly int _timeoutMilliseconds;
    private readonly Func<ProcessStartInfo, Process> _start;

    internal SharedWorkerProcessDump(string executable, string directory,
        int timeoutMilliseconds = 10000, Func<ProcessStartInfo, Process> start = null)
    {
        _executable = executable;
        _directory = directory;
        _timeoutMilliseconds = timeoutMilliseconds;
        _start = start ?? Process.Start;
    }

    internal static Func<string> FromEnvironment(string directory)
    {
        var executable = Environment.GetEnvironmentVariable("LITEDB_SHARED_PROCDUMP");
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            return () => "ProcDump capture skipped: the configured tool requires Windows.";
        return new SharedWorkerProcessDump(executable, directory).Capture;
    }

    internal string Capture()
    {
        using var current = Process.GetCurrentProcess();
        var name = "dump-" + current.Id + "-" + Guid.NewGuid().ToString("N");
        var dump = Path.Combine(_directory, name + ".dmp");
        // ProcDump defaults to an x86 dump for an x86 process. -64 would instead
        // select WOW64 subsystem diagnostics and must not be used here.
        // A snapshot clone (-r) lets this timeout coordinator continue during I/O.
        var arguments = $"-accepteula -ma -r -at 5 {current.Id} \"{dump}\"";
        var report = new StringBuilder()
            .AppendLine($"Dump requested UTC: {DateTime.UtcNow:O}; PID={current.Id}; processBits={IntPtr.Size * 8}")
            .AppendLine("Worker deadline has already failed; cancellation follows this diagnostic attempt (10-second process-wait budget; OS start and file I/O may add delay).")
            .AppendLine($"Command: {_executable} {arguments}");
        var output = new StringBuilder();
        var elapsed = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(_directory);
            using var process = _start(StartInfo(arguments, redirect: true));
            if (process == null) throw new InvalidOperationException("ProcDump did not start.");
            process.OutputDataReceived += (_, value) => AppendOutput(output, value.Data);
            process.ErrorDataReceived += (_, value) => AppendOutput(output, value.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Reserve part of the ten-second process-wait budget for graceful cancellation and
            // cleanup. No unbounded WaitForExit or pipe drain runs on this path.
            if (!process.WaitForExit(Math.Max(1, _timeoutMilliseconds * 7 / 10)))
            {
                report.AppendLine("Dump process timed out; requesting graceful ProcDump cancellation.");
                Cancel(process, current.Id, elapsed, report);
            }
            if (process.HasExited) report.AppendLine($"Dump exit code: {process.ExitCode}");
            else report.AppendLine("Dump process did not exit within the diagnostic process-wait budget.");

            // Signed ProcDump 12.01 returned 1 after completing one dump in the
            // Windows smoke. This is an observed tool contract, not a generic
            // nonzero-success assumption: require complete matching dump data.
            var valid = process.HasExited && (process.ExitCode == 0 || process.ExitCode == 1) &&
                SharedWorkerDumpFormat.Validate(dump, current.Id, IntPtr.Size * 8);
            report.AppendLine($"Dump validated full process structure: {valid}; path={dump}");
            if (File.Exists(dump)) report.AppendLine($"Dump bytes: {new FileInfo(dump).Length}");
        }
        catch (Exception ex)
        {
            // Missing tools, access errors, failed captures, and failed logging
            // must never replace the original worker timeout or skip cancellation.
            report.AppendLine($"Dump capture failed: {ex.GetType().Name}: {ex.Message}");
        }
        lock (output) report.Append(output);
        report.AppendLine($"Dump attempt elapsedMs: {elapsed.ElapsedMilliseconds}; finished UTC: {DateTime.UtcNow:O}");
        try { File.WriteAllText(Path.Combine(_directory, name + ".txt"), report.ToString()); }
        catch (Exception ex) { report.AppendLine($"Dump report write failed: {ex.GetType().Name}: {ex.Message}"); }
        return report.ToString();
    }

    private void Cancel(Process process, int targetPid, Stopwatch elapsed, StringBuilder report)
    {
        var cleanupSlice = Math.Max(1, _timeoutMilliseconds / 10);
        try
        {
            // ProcDump's cancellation event resumes a capture target safely; do
            // not kill the testhost or treat evidence failure as a product crash.
            using var cancel = _start(StartInfo("-cancel " + targetPid, redirect: false));
            if (cancel != null && !cancel.WaitForExit(Remaining(elapsed, cleanupSlice))) cancel.Kill();
        }
        catch (Exception ex) { report.AppendLine($"Graceful dump cancellation failed: {ex.GetType().Name}: {ex.Message}"); }
        try
        {
            if (!process.WaitForExit(Remaining(elapsed, cleanupSlice)))
            {
                process.Kill();
                process.WaitForExit(Remaining(elapsed, cleanupSlice));
            }
        }
        catch (Exception ex) { report.AppendLine($"Dump process cleanup failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private int Remaining(Stopwatch elapsed, int maximum) =>
        (int)Math.Max(1, Math.Min(maximum, _timeoutMilliseconds - elapsed.ElapsedMilliseconds));

    private ProcessStartInfo StartInfo(string arguments, bool redirect) => new ProcessStartInfo
    {
        FileName = _executable,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = redirect,
        RedirectStandardError = redirect,
        // ProcDump 12.01 writes UTF-16LE when its console output is redirected.
        StandardOutputEncoding = redirect ? Encoding.Unicode : null,
        StandardErrorEncoding = redirect ? Encoding.Unicode : null
    };

    private static void AppendOutput(StringBuilder output, string line)
    {
        if (line == null) return;
        lock (output)
        {
            if (output.Length < 64 * 1024) output.AppendLine(line.Substring(0, Math.Min(line.Length, 4096)));
        }
    }

}
