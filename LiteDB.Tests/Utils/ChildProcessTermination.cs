using System;
using System.Collections.Generic;
using System.Diagnostics;
#if !NETFRAMEWORK
using System.Threading.Tasks;
#endif

namespace LiteDB.Tests
{
    internal static class ChildProcessTermination
    {
        // The synchronous form also serves the optional diagnostic collector, whose entire
        // cancellation (exit plus file release) must fit in its remaining process-wait budget.
        internal static void Kill(Process process, IEnumerable<string> files, int timeoutMilliseconds,
            bool entireProcessTree = false)
        {
            var elapsed = Stopwatch.StartNew();
#if NETFRAMEWORK
            process.Kill();
#else
            process.Kill(entireProcessTree);
#endif
            if (!process.WaitForExit(Math.Max(1, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds)))
                throw new TimeoutException("Killed child did not exit.");
            WindowsFileRelease.WaitForFiles(files,
                Math.Max(0, Math.Min(5000, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds)));
        }

#if !NETFRAMEWORK
        internal static async Task KillAsync(Process process, IEnumerable<string> files)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            WindowsFileRelease.WaitForFiles(files);
        }
#endif
    }
}
