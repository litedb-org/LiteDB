#if !NETFRAMEWORK
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LiteDB.Internals
{
    // Diagnostic overlay only; never installed into production code.
    internal static class RetryDiagnostics
    {
        internal static void SaveChild(int pid, string errors)
        {
            var directory = Environment.GetEnvironmentVariable("F_RETRY_EVIDENCE");
            if (directory != null) File.WriteAllText(Path.Combine(directory, $"child-{pid}.txt"), errors);
        }

        internal static async Task Capture(int child)
        {
            var directory = Environment.GetEnvironmentVariable("F_RETRY_EVIDENCE");
            if (directory == null) return;
            File.AppendAllText(Path.Combine(directory, "timeouts.txt"),
                $"{DateTime.UtcNow:O} parent={Environment.ProcessId} child={child}\n");
            // Both processes remain alive until both attempts finish. Capture concurrently
            // so collecting the parent cannot delay the first view of the child's stall.
            await Task.WhenAll(CaptureOne(child, directory), CaptureOne(Environment.ProcessId, directory));
        }

        private static async Task CaptureOne(int pid, string directory)
        {
            foreach (var tool in new[] { "dotnet-stack", "dotnet-dump" })
            {
                var path = Environment.GetEnvironmentVariable("F_RETRY_TOOLS");
                var executable = Path.Combine(path, tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
                var log = Path.Combine(directory, $"{tool}-{pid}.txt");
                try
                {
                    var start = new ProcessStartInfo(executable)
                    {
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    start.ArgumentList.Add(tool == "dotnet-stack" ? "report" : "collect");
                    start.ArgumentList.Add("-p");
                    start.ArgumentList.Add(pid.ToString());
                    if (tool == "dotnet-dump")
                    {
                        start.ArgumentList.Add("--type"); start.ArgumentList.Add("Mini");
                        start.ArgumentList.Add("-o"); start.ArgumentList.Add(Path.Combine(directory, $"dump-{pid}.dmp"));
                    }
                    using var process = Process.Start(start);
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
                    catch (TimeoutException)
                    {
                        // Only the diagnostic tool is stopped here, never the test workers.
                        process.Kill();
                        await process.WaitForExitAsync();
                    }
                    File.WriteAllText(log, await output + await errors + $"\nexit={process.ExitCode}\n");
                    if (tool == "dotnet-stack" && process.ExitCode == 0) break;
                }
                catch (Exception error) { File.WriteAllText(log, error.ToString()); }
            }
        }
    }
}
#endif
