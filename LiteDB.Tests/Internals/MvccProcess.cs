#if !NETFRAMEWORK
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
            // Use the host beside the runtime executing this test, including
            // isolated CI installations and Windows x86. PATH may select x64
            // or a newer major runtime even when the parent guard is correct.
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var host = Path.Combine(runtime.Parent.Parent.Parent.FullName,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment["DOTNET_ROLL_FORWARD"] = "Disable";
            start.Environment["LITEDB_MVCC_RUNTIME"] = Environment.Version.ToString();
            start.Environment["LITEDB_MVCC_ARCHITECTURE"] = RuntimeInformation.ProcessArchitecture.ToString();
            start.ArgumentList.Add("--fx-version");
            start.ArgumentList.Add(Environment.Version.ToString());
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

        /// <summary>Next output line, or null once the process closed its output.</summary>
        internal Task<string> ReadLine(TimeSpan timeout) =>
            _process.StandardOutput.ReadLineAsync().WaitAsync(timeout);

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
