using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace LiteDB.Tests.Engine;

/// <summary>Smoke-only retries; real worker timeout capture remains single-shot.</summary>
internal static class SharedWorkerDumpSmoke
{
    internal static string Capture(string directory, Func<string, Func<string>> createCapture)
    {
        using var current = Process.GetCurrentProcess();
        var summary = new StringBuilder();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var attemptDirectory = Path.Combine(directory, $"attempt-{attempt}-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attemptDirectory);
            var capture = createCapture(attemptDirectory) ??
                throw new InvalidOperationException("The dump smoke requires a configured capture tool.");
            var diagnostics = new SharedWorkerDiagnostics(attemptDirectory, true, capture);
            diagnostics.Track("capture smoke").Progress("intentional timeout capture", 0);
            var report = diagnostics.Timeout("capture smoke");
            File.WriteAllText(Path.Combine(attemptDirectory, "smoke-capture-report.txt"), report);

            var dumps = Directory.GetFiles(attemptDirectory, "*.dmp");
            var valid = dumps.Length == 1 &&
                report.Split('\n').Any(line => line.TrimEnd('\r') ==
                    "Dump validated full process structure: True; path=" + dumps[0]) &&
                SharedWorkerDumpFormat.Validate(dumps[0], current.Id, IntPtr.Size * 8);
            summary.AppendLine($"Smoke attempt {attempt}/3: valid={valid}; directory={attemptDirectory}")
                .AppendLine(report);
            File.WriteAllText(Path.Combine(directory, "smoke-summary.txt"), summary.ToString());
            if (!valid) continue;

            File.Delete(dumps[0]);
            summary.AppendLine("Successful smoke dump removed after validating full dump structure, process identity and architecture; all attempt reports and failed dumps retained.");
            File.WriteAllText(Path.Combine(directory, "successful-smoke.txt"), summary.ToString());
            return summary.ToString();
        }
        throw new InvalidOperationException("All three dump smoke attempts were invalid.\n" + summary);
    }
}
