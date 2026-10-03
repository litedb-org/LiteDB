using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace LiteDB.ConcurrencyTesting
{
    /// <summary>
    /// Parent side of the <c>process=external-writer</c> dimension: another process holding a
    /// Shared connection to the fixture, driven by <see cref="ExplorerWriterChild"/>'s line protocol.
    /// Each command waits at most <see cref="ExplorerSchedule.LockBound"/> for its reply.
    /// </summary>
    internal sealed class ExternalWriter : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;

        private ExternalWriter(Process process)
        {
            _process = process;
            _errors = process.StandardError.ReadToEndAsync();
            this.Expect("ready", "start");
        }

        internal static string NotApplicable(ExplorerConfiguration configuration, IExplorerHost host)
        {
            if (!configuration.Shared) return "process=external-writer needs mode=shared (Direct connections exclude other processes)";
            if (host.ExternalWriter("probe") == null) return "this host cannot start an external writer process";
            return null;
        }

        internal static ExternalWriter Start(IExplorerHost host, ExplorerModel model)
        {
            var start = host.ExternalWriter(model.Path);
            start.UseShellExecute = false;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.Environment[ExplorerWriterChild.PasswordVariable] = model.Encrypted ? ExplorerModel.Password : "";
            return new ExternalWriter(Process.Start(start) ?? throw new InvalidOperationException("external writer did not start"));
        }

        /// <summary>The standard harness start info: the test harness process in mode explorer-writer (not on .NET Framework).</summary>
        internal static ProcessStartInfo HarnessStartInfo(string path)
        {
#if NETFRAMEWORK
            return null;
#else
            var harness = Path.Combine(AppContext.BaseDirectory, "mvcc-probe", "SharedMutexHarness.dll");
            if (!File.Exists(harness)) return null;
            var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var dotnet = Path.Combine(runtime.Parent.Parent.Parent.FullName,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet");
            var start = new ProcessStartInfo(dotnet);
            start.Environment["DOTNET_ROLL_FORWARD"] = "Disable";
            start.Environment["LITEDB_MVCC_RUNTIME"] = Environment.Version.ToString();
            start.Environment["LITEDB_MVCC_ARCHITECTURE"] = RuntimeInformation.ProcessArchitecture.ToString();
            foreach (var argument in new[] { "--fx-version", Environment.Version.ToString(), harness, "mvcc", ExplorerWriterChild.Mode, path, "-" })
                start.ArgumentList.Add(argument);
            return start;
#endif
        }

        /// <summary>Auto-commit upsert in the other process; true when it acknowledged, false when it failed.</summary>
        internal bool Write(string collection, int id, int value) => this.Command($"write {collection} {id} {value}") == "ok";

        /// <summary>BeginTrans + upsert in the other process, holding its writer ownership until <see cref="Finish"/>.</summary>
        internal bool Hold(string collection, int id, int value)
        {
            var reply = this.Command($"hold {collection} {id} {value}");
            return reply == "held";
        }

        internal bool Finish(bool commit) => this.Command(commit ? "commit" : "rollback") == "ok";

        private string Command(string command)
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
            return this.Expect(null, command);
        }

        private string Expect(string expected, string after)
        {
            var line = _process.StandardOutput.ReadLineAsync();
            if (!line.Wait(ExplorerSchedule.LockBound))
                throw new ExplorerFailure("DEADLINE_EXTERNAL_WRITER", "the external writer did not answer '" + after + "' in time");
            if (line.Result == null)
                throw new ExplorerFailure("EXPLORER_EXTERNAL_WRITER_EXITED", "after '" + after + "': " + _errors.Result);
            if (expected != null && line.Result != expected)
                throw new ExplorerFailure("EXPLORER_EXTERNAL_WRITER_PROTOCOL", "expected " + expected + ", got " + line.Result);
            return line.Result;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.StandardInput.WriteLine("exit");
                    _process.StandardInput.Flush();
                    if (!_process.WaitForExit((int)ExplorerSchedule.LockBound.TotalMilliseconds))
                    {
                        _process.Kill();
                        throw new ExplorerFailure("DEADLINE_EXTERNAL_WRITER_EXIT", "the external writer did not exit");
                    }
                }
                if (_process.ExitCode != 0)
                    throw new ExplorerFailure("EXPLORER_EXTERNAL_WRITER_FAILED", "exit code " + _process.ExitCode + ": " + _errors.Result);
            }
            finally { _process.Dispose(); }
        }
    }
}
