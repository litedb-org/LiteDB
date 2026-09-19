#if !NETFRAMEWORK
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;

namespace LiteDB.Internals
{
    internal sealed class MvccProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;

        internal MvccProcess(string mode, string filename, string password, string value = null)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "mvcc-probe", "SharedMutexHarness.dll"));
            foreach (var arg in new[] { "mvcc", mode, filename, password ?? "-" }) start.ArgumentList.Add(arg);
            if (value != null) start.ArgumentList.Add(value);
            _process = Process.Start(start);
            _errors = _process.StandardError.ReadToEndAsync();
        }

        internal async Task Expect(string expected)
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (line == null) throw new Exception(await _errors);
            line.Should().Be(expected);
        }

        internal async Task Finish(bool release = false)
        {
            if (release) _process.StandardInput.WriteLine("continue");
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _process.ExitCode.Should().Be(0, await _errors);
        }

        internal async Task Kill()
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }

        internal static async Task Run(string mode, string filename, string password, string value = null)
        {
            using var process = new MvccProcess(mode, filename, password, value);
            await process.Expect("done");
            await process.Finish();
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10000);
            }
            _process.Dispose();
        }
    }
}
#endif
