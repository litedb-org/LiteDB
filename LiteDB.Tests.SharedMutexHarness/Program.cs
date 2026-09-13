using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using LiteDB;

var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath could not be determined.");
var options = HarnessOptions.Parse(args, executablePath);

if (options.Mode == HarnessMode.Child)
{
    RunChild(options);
    return;
}

RunParent(options);
return;

void RunParent(HarnessOptions options)
{
    Console.WriteLine($"[parent] creating shared mutex '{options.MutexName}'");
    if (options.UsePsExec)
    {
        Console.WriteLine($"[parent] PsExec mode enabled (session {options.SessionId}, tool: {options.PsExecPath})");
    }

    Directory.CreateDirectory(options.LogDirectory);

    using var mutex = CreateSharedMutex(options.MutexName);

    Console.WriteLine("[parent] acquiring mutex");
    mutex.WaitOne();
    Console.WriteLine("[parent] mutex acquired");

    Console.WriteLine("[parent] spawning child with 2s timeout while mutex is held");
    var probeResult = StartChildProcess(options, waitMilliseconds: 2000, "probe");
    Console.WriteLine(probeResult);

    Console.WriteLine("[parent] releasing mutex");
    mutex.ReleaseMutex();

    Console.WriteLine("[parent] spawning child waiting without timeout after release");
    var acquireResult = StartChildProcess(options, waitMilliseconds: -1, "post-release");
    Console.WriteLine(acquireResult);

    Console.WriteLine("[parent] experiment finished");
}

string StartChildProcess(HarnessOptions options, int waitMilliseconds, string label)
{
    var logPath = Path.Combine(options.LogDirectory, $"{label}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.log");

    var psi = BuildChildStartInfo(options, waitMilliseconds, logPath);

    using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to spawn child process.");

    var output = new StringBuilder();
    process.OutputDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            output.AppendLine(e.Data);
        }
    };
    process.ErrorDataReceived += (_, e) =>
    {
        if (e.Data != null)
        {
            output.AppendLine("[stderr] " + e.Data);
        }
    };

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    if (!process.WaitForExit(10000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("Child process exceeded wait timeout.");
    }

    process.WaitForExit();

    if (File.Exists(logPath))
    {
        output.AppendLine("[child log]");
        output.Append(File.ReadAllText(logPath));
        File.Delete(logPath);
    }
    else
    {
        output.AppendLine("[child log missing]");
    }

    return output.ToString();
}

ProcessStartInfo BuildChildStartInfo(HarnessOptions options, int waitMilliseconds, string logPath)
{
    if (!options.UsePsExec)
    {
        var directStartInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        directStartInfo.ArgumentList.Add("child");
        directStartInfo.ArgumentList.Add(options.MutexName);
        directStartInfo.ArgumentList.Add(waitMilliseconds.ToString());
        directStartInfo.ArgumentList.Add(logPath);

        return directStartInfo;
    }

    if (!File.Exists(options.PsExecPath))
    {
        throw new FileNotFoundException("PsExec executable could not be located.", options.PsExecPath);
    }

    var psi = new ProcessStartInfo(options.PsExecPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    psi.ArgumentList.Add("-accepteula");
    psi.ArgumentList.Add("-nobanner");
    psi.ArgumentList.Add("-i");
    psi.ArgumentList.Add(options.SessionId.ToString());

    if (options.RunAsSystem)
    {
        psi.ArgumentList.Add("-s");
    }

    psi.ArgumentList.Add(options.ExecutablePath);
    psi.ArgumentList.Add("child");
    psi.ArgumentList.Add(options.MutexName);
    psi.ArgumentList.Add(waitMilliseconds.ToString());
    psi.ArgumentList.Add(logPath);

    return psi;
}

void RunChild(HarnessOptions options)
{
    if (!string.IsNullOrEmpty(options.LogPath))
    {
        var directory = Path.GetDirectoryName(options.LogPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    void Log(string message)
    {
        Console.WriteLine(message);
        if (!string.IsNullOrEmpty(options.LogPath))
        {
            File.AppendAllText(options.LogPath, message + Environment.NewLine);
        }
    }

    using var mutex = CreateSharedMutex(options.MutexName);
    Log($"[child {Environment.ProcessId}] attempting to acquire mutex '{options.MutexName}' (wait={options.ChildWaitMilliseconds}ms)");

    var sw = Stopwatch.StartNew();
    bool acquired;

    if (options.ChildWaitMilliseconds >= 0)
    {
        acquired = mutex.WaitOne(options.ChildWaitMilliseconds);
    }
    else
    {
        acquired = mutex.WaitOne();
    }

    sw.Stop();

    Log($"[child {Environment.ProcessId}] acquired={acquired} after {sw.ElapsedMilliseconds}ms");

    if (acquired)
    {
        mutex.ReleaseMutex();
        Log($"[child {Environment.ProcessId}] released mutex");
    }
}

static Mutex CreateSharedMutex(string name)
{
    var liteDbAssembly = typeof(SharedEngine).Assembly;
    var factoryType = liteDbAssembly.GetType("LiteDB.SharedMutexFactory", throwOnError: true)
        ?? throw new InvalidOperationException("Could not locate SharedMutexFactory.");

    var createMethod = factoryType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not resolve the Create method on SharedMutexFactory.");

    var mutex = createMethod.Invoke(null, new object[] { name }) as Mutex;

    if (mutex is null)
    {
        throw new InvalidOperationException("SharedMutexFactory.Create returned null.");
    }

    return mutex;
}

internal sealed record HarnessOptions(
    HarnessMode Mode,
    string MutexName,
    bool UsePsExec,
    int SessionId,
    bool RunAsSystem,
    string PsExecPath,
    string ExecutablePath,
    int ChildWaitMilliseconds,
    string? LogPath,
    string LogDirectory)
{
    public static HarnessOptions Parse(string[] args, string executablePath)
    {
        bool usePsExec = false;
        int sessionId = 0;
        string? psExecPath = null;
        bool runAsSystem = false;
        string? logDirectory = null;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--use-psexec", StringComparison.OrdinalIgnoreCase))
            {
                usePsExec = true;
                continue;
            }

            if (string.Equals(arg, "--session", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing session identifier after --session.");
                }

                sessionId = int.Parse(args[++i]);
                continue;
            }

            if (arg.StartsWith("--psexec-path=", StringComparison.OrdinalIgnoreCase))
            {
                psExecPath = arg.Substring("--psexec-path=".Length);
                continue;
            }

            if (string.Equals(arg, "--system", StringComparison.OrdinalIgnoreCase))
            {
                runAsSystem = true;
                continue;
            }

            if (arg.StartsWith("--log-dir=", StringComparison.OrdinalIgnoreCase))
            {
                logDirectory = arg.Substring("--log-dir=".Length);
                continue;
            }

            positional.Add(arg);
        }

        var mutexName = positional.Count > 1
            ? positional[1]
            : positional.Count == 1 && !string.Equals(positional[0], "child", StringComparison.OrdinalIgnoreCase)
                ? positional[0]
                : "LiteDB_SharedMutexHarness";

        if (positional.Count > 0 && string.Equals(positional[0], "child", StringComparison.OrdinalIgnoreCase))
        {
            if (positional.Count < 3)
            {
                throw new ArgumentException("Child invocation expects mutex name and wait duration.");
            }

            var waitMilliseconds = int.Parse(positional[2]);
            var logPath = positional.Count >= 4 ? positional[3] : null;
            var childLogDirectory = logDirectory
                ?? (logPath != null
                    ? Path.GetDirectoryName(logPath) ?? DefaultLogDirectory()
                    : DefaultLogDirectory());

            return new HarnessOptions(
                HarnessMode.Child,
                positional[1],
                UsePsExec: false,
                sessionId,
                runAsSystem,
                psExecPath ?? DefaultPsExecPath(),
                executablePath,
                waitMilliseconds,
                logPath,
                childLogDirectory);
        }

        var resolvedLogDirectory = logDirectory ?? DefaultLogDirectory();

        return new HarnessOptions(
            HarnessMode.Parent,
            mutexName,
            usePsExec,
            sessionId,
            runAsSystem,
            psExecPath ?? DefaultPsExecPath(),
            executablePath,
            ChildWaitMilliseconds: -1,
            LogPath: null,
            LogDirectory: resolvedLogDirectory);
    }

    private static string DefaultPsExecPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "tools", "Sysinternals", "PsExec.exe");
    }

    private static string DefaultLogDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "SharedMutexHarnessLogs");
    }
}

internal enum HarnessMode
{
    Parent,
    Child
}
