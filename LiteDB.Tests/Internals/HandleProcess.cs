using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
#if !NETFRAMEWORK
using System.Runtime.InteropServices;
#endif

namespace LiteDB.Internals
{
    // Runs the matching CLR and architecture, including CLR 4 fallback legs.
    internal sealed class HandleProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        internal HandleProcess(string mode, string file, string password, string value = null)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "handle-probe");
#if NETFRAMEWORK
            var start = new ProcessStartInfo(Path.Combine(directory, "SharedMutexHarness.exe"));
#else
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var start = new ProcessStartInfo(Path.Combine(runtime.Parent.Parent.Parent.FullName,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet"));
            start.ArgumentList.Add("--fx-version");
            start.ArgumentList.Add(Environment.Version.ToString());
            start.ArgumentList.Add(Path.Combine(directory, "SharedMutexHarness.dll"));
#endif
            start.UseShellExecute = false;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            var args = new[] { "mvcc", mode, file, password ?? "-", value ?? "-" };
#if NETFRAMEWORK
            start.Arguments = string.Join(" ", Array.ConvertAll(args, arg => "\"" + arg.Replace("\"", "\\\"") + "\""));
#else
            foreach (var arg in args) start.ArgumentList.Add(arg);
#endif
            _process = Process.Start(start);
            _errors = _process.StandardError.ReadToEndAsync();
        }
        internal void Send(string command) => _process.StandardInput.WriteLine(command);
        private bool _runtimeChecked;
        internal async Task Expect(string expected)
        {
            if (!_runtimeChecked)
            {
                _runtimeChecked = true;
                await Expect("runtime:" + Environment.Version.Major + ":" + IntPtr.Size);
            }
            var read = _process.StandardOutput.ReadLineAsync();
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(20000)));
            var line = await read;
            Assert.True(line != null, line == null ? await _errors : "");
            Assert.Equal(expected, line);
        }
        internal Task Finish()
        {
            Assert.True(_process.WaitForExit(20000), "Child exit deadline");
            Assert.Equal(0, _process.ExitCode);
            return Task.CompletedTask;
        }
        internal Task Kill()
        {
            _process.Kill();
            Assert.True(_process.WaitForExit(20000));
            return Task.CompletedTask;
        }
        internal static async Task Run(string mode, string file, string password, string value)
        {
            using var child = new HandleProcess(mode, file, password, value);
            await child.Expect("done"); await child.Finish();
        }
        public void Dispose()
        {
            if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(10000); }
            _process.Dispose();
        }
    }
}
