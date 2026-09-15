using System.Diagnostics;
using System.Security.Cryptography;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

internal static class Program
{
    private static async Task<int> Main()
    {
        ReproConfigurationReporter.SendConfiguration(ReproHostClient.CreateDefault());
        var directory = Path.Combine(Path.GetTempPath(), "litedb-2794-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var filename = Path.Combine(directory, "jobs-" + attempt + ".db");
                await RunWorker("seed", filename);
                using (var service = new Worker("service", filename))
                using (var client = new Worker("client", filename))
                {
                    var ready = Stopwatch.StartNew();
                    while (!File.Exists(filename + ".client.ready") || !File.Exists(filename + ".service.ready"))
                    {
                        if (client.HasExited || service.HasExited || ready.Elapsed > TimeSpan.FromSeconds(15))
                        {
                            await Task.WhenAll(client.StopAndPrint(), service.StopAndPrint());
                            throw new InvalidOperationException("Both worker processes must reach the start barrier.");
                        }
                        await Task.Delay(10);
                    }
                    File.WriteAllText(filename + ".go", "start");
                    await Task.WhenAll(client.Complete(), service.Complete());
                }
                var before = SHA256.HashData(File.ReadAllBytes(filename));
                await RunWorker("verify", filename);
                var after = SHA256.HashData(File.ReadAllBytes(filename));
                if (!before.SequenceEqual(after)) throw new InvalidOperationException("Read-only verification changed the database.");
                Console.WriteLine($"ATTEMPT_{attempt}: two processes, 24 rows, 24 rounds, 1152 acknowledged updates; fresh-process ledger and read-only bytes passed");
            }
            Console.WriteLine("VERIFIED_2794: attempts=3, reportedFailures=0; this bounded workload does not establish the unknown corruption origin");
            return 10;
        }
        catch (Exception error)
        {
            // Any actual read failure is retained for diagnosis, never automatically
            // substituted for the issue's unknown originating write history.
            Console.Error.WriteLine(error);
            return 20;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunWorker(string mode, string filename)
    {
        using var worker = new Worker(mode, filename);
        await worker.Complete();
    }

    private sealed class Worker : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly string _mode;
        private bool _printed;

        internal bool HasExited => _process.HasExited;

        internal Worker(string mode, string filename)
        {
            _mode = mode;
            var windows = OperatingSystem.IsWindows();
            var assembly = Path.Combine(AppContext.BaseDirectory, "worker", "Issue2794.Worker" + (windows ? ".exe" : ".dll"));
            var start = new ProcessStartInfo(windows ? assembly : "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (!windows) start.ArgumentList.Add(assembly);
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(filename);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
        }

        internal async Task Complete()
        {
            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            }
            catch
            {
                await StopAndPrint();
                throw;
            }
            await Print();
            if (_process.ExitCode != 0 || !(await _stdout).Contains("VERIFIED_WORKER_2794 mode=" + _mode + ", rows=24, rounds=24"))
            {
                throw new InvalidOperationException($"{_mode} worker failed: exit {_process.ExitCode}.");
            }
        }

        internal async Task StopAndPrint()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await Print();
        }

        private async Task Print()
        {
            if (_printed) return;
            _printed = true;
            Console.Write(await _stdout);
            Console.Error.Write(await _stderr);
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
            _process.Dispose();
        }
    }
}
