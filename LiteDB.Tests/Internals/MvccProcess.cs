#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests;
using LiteDB.Client.Shared;

namespace LiteDB.Internals
{
    internal sealed class MvccProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        private readonly string _filename;
        private readonly HashSet<string> _existingReaders;
        private readonly HashSet<string> _childReaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal MvccProcess(string mode, string filename, string password, string value = null, bool disableFileLocking = false, bool disableMappedReads = false)
        {
            _filename = filename;
            _existingReaders = new HashSet<string>(ReaderFiles(), StringComparer.OrdinalIgnoreCase);
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
            if (disableFileLocking) start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
            if (disableMappedReads) start.Environment["LITEDB_DISABLE_SHARED_MAPPED_READS"] = "1";
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

        internal int ProcessId => _process.Id;

        internal async Task Expect(string expected)
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (line == null) throw new Exception(await _errors);
            line.Should().Be(expected);
            // These barriers precede starting a peer. Remember newly created child-private
            // leases so a kill can settle them even when another participant keeps the DB open.
            foreach (var path in ReaderFiles())
                if (!_existingReaders.Contains(path)) _childReaders.Add(path);
        }

        /// <summary>Next output line; include child stderr if its output closes unexpectedly.</summary>
        internal async Task<string> ReadLine(TimeSpan timeout)
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            if (line == null) throw new Exception(await _errors);
            return line;
        }

        internal void Send(string command) => _process.StandardInput.WriteLine(command);

        internal async Task Finish(bool release = false)
        {
            if (release) _process.StandardInput.WriteLine("continue");
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _process.ExitCode.Should().Be(0, await _errors);
        }

        internal async Task Kill(bool sharedFilesRemainOpen = false)
        {
            var paths = ReleaseFiles(sharedFilesRemainOpen);
            await ChildProcessTermination.KillAsync(_process, paths);
        }

        private string[] ReaderFiles()
        {
            if (!WindowsFileRelease.IsWindows) return Array.Empty<string>();
            try { return Directory.GetFiles(_filename + "-readers"); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        }

        private string[] ReleaseFiles(bool sharedFilesRemainOpen)
        {
            if (!WindowsFileRelease.IsWindows) return Array.Empty<string>();
            // Shared data/control files cannot be opened exclusively while an intentional
            // parent/peer survives. In those scenarios probe only the child's private leases.
            if (sharedFilesRemainOpen) return _childReaders.ToArray();
            var controls = new[] { _filename + "-shared-live", _filename + "-shared-state", _filename + "-shared-disabled" };
            var paths = new List<string> { _filename, FileHelper.GetLogFile(_filename) };
            paths.AddRange(controls);
            paths.AddRange(ReaderFiles());
            foreach (var control in controls)
            {
                // CreateName only computes a name. Use its database-specific prefix so an
                // interrupted publication's open temporary is probed without touching peers.
                var temporary = SharedCoordinationTemporaryFiles.CreateName(control);
                var name = Path.GetFileName(temporary);
                paths.AddRange(Directory.GetFiles(Path.GetDirectoryName(temporary),
                    name.Substring(0, name.LastIndexOf('-') + 1) + "*"));
            }
            return paths.ToArray();
        }

        internal static async Task Run(string mode, string filename, string password, string value = null)
        {
            using var process = new MvccProcess(mode, filename, password, value);
            await process.Expect("done");
            await process.Finish();
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    // Failure cleanup can run while parent/peer participants are still alive.
                    var paths = ReleaseFiles(sharedFilesRemainOpen: true);
                    ChildProcessTermination.Kill(_process, paths, 10000, entireProcessTree: true);
                }
            }
            finally { _process.Dispose(); }
        }
    }
}
#endif
