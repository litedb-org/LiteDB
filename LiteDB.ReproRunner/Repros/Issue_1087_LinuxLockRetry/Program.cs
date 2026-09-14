using System.Diagnostics;
using System.Runtime.Versioning;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_1087_LinuxLockRetry;

internal static class Program
{
    private const int ExpectedLinuxLockError = 11;
    private const int FixedOutcomeExitCode = 10;
    private const int MaximumExpectedRetryAttempts = 200;
    private const long LockOffset = 0;
    private const long LockLength = 1;

    private static int Main()
    {
        var host = ReproHostClient.CreateDefault();
        ReproConfigurationReporter.SendConfiguration(host);
        var context = ReproContext.FromEnvironment();

        host.SendLifecycle("starting", new
        {
            context.InstanceIndex,
            context.TotalInstances,
            context.SharedDatabaseRoot
        });

        try
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("Issue 1087 requires Linux file-lock semantics.");
            }

            if (context.TotalInstances != 2)
            {
                throw new InvalidOperationException(
                    $"Issue 1087 requires exactly two processes; received {context.TotalInstances}.");
            }

            var paths = ScenarioPaths.Create(context.SharedDatabaseRoot);
            var exitCode = context.InstanceIndex == 0
                ? RunOwner(host, paths)
                : RunContender(host, paths);

            host.SendLifecycle("completed", new { Success = exitCode == 0, ExitCode = exitCode });
            return exitCode;
        }
        catch (Exception ex)
        {
            host.SendLog($"Repro harness failed: {ex}", ReproHostLogLevel.Error);
            host.SendResult(false, "Issue 1087 repro harness failed.", new { Exception = ex.ToString() });
            host.SendLifecycle("completed", new { Success = false });
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private static int RunOwner(ReproHostClient host, ScenarioPaths paths)
    {
        Directory.CreateDirectory(paths.Root);

        using var stream = new FileStream(
            paths.Database,
            System.IO.FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        stream.SetLength(1);
        stream.Flush(true);
        stream.Lock(LockOffset, LockLength);
        var ownsLock = true;

        try
        {
            File.WriteAllText(paths.Ready, "locked");
            host.SendLog("Owner acquired the byte-range lock.");

            WaitForFile(paths.ReleaseRequest, TimeSpan.FromSeconds(15));

            // Keep the collision present long enough to distinguish an immediate propagation
            // from LiteDB's 25 ms retry cadence without relying on a razor-thin timing bound.
            Thread.Sleep(500);
            stream.Unlock(LockOffset, LockLength);
            ownsLock = false;
            File.WriteAllText(paths.Released, "released");
            host.SendLog("Owner released the byte-range lock after the controlled delay.");

            WaitForFile(paths.ContenderDone, TimeSpan.FromSeconds(15));
            host.SendResult(true, "Lock-owner process completed its half of the scenario.");
            return 0;
        }
        finally
        {
            if (ownsLock)
            {
                stream.Unlock(LockOffset, LockLength);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private static int RunContender(ReproHostClient host, ScenarioPaths paths)
    {
        WaitForFile(paths.Ready, TimeSpan.FromSeconds(15));

        using var stream = new FileStream(
            paths.Database,
            System.IO.FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        try
        {
            LiteDbRetryBridge.VerifyControls();
            host.SendLog(
                "Classifier controls passed, and the non-lock IOException propagated after exactly one attempt.");

            var collision = CaptureCollision(stream);
            var nativeCode = GetNativeCode(collision);

            if (nativeCode != ExpectedLinuxLockError)
            {
                throw new InvalidOperationException(
                    $"The real Linux lock collision returned code {nativeCode}, not {ExpectedLinuxLockError}.");
            }

            var classifiedAsLocked = LiteDbRetryBridge.IsLocked(collision);
            host.SendLog(
                $"Raw lock control returned HResult=0x{collision.HResult:X8}, nativeCode={nativeCode}; " +
                $"LiteDB classifiedAsLocked={classifiedAsLocked}.");

            File.WriteAllText(paths.ReleaseRequest, "release after delay");

            var attempts = 0;
            var acquired = false;
            Exception? retryException = null;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                LiteDbRetryBridge.Invoke(() =>
                {
                    attempts++;
                    stream.Lock(LockOffset, LockLength);
                    acquired = true;
                }, TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                retryException = ex;
            }
            finally
            {
                stopwatch.Stop();
                WaitForFile(paths.Released, TimeSpan.FromSeconds(10));
            }

            if (acquired)
            {
                stream.Unlock(LockOffset, LockLength);
            }

            stream.Lock(LockOffset, LockLength);
            stream.Unlock(LockOffset, LockLength);
            var postReleaseControlAcquired = true;

            host.SendLog(
                $"Retry outcome: attempts={attempts}, acquired={acquired}, " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}, exception={retryException?.GetType().FullName ?? "none"}; " +
                $"postReleaseControlAcquired={postReleaseControlAcquired}.");

            if (!classifiedAsLocked &&
                !acquired &&
                postReleaseControlAcquired &&
                attempts == 1 &&
                retryException is IOException ioException &&
                GetNativeCode(ioException) == ExpectedLinuxLockError)
            {
                const string marker = "BUG_1087_CONFIRMED";
                Console.WriteLine(marker);
                host.SendResult(true, marker, new
                {
                    NativeCode = nativeCode,
                    Attempts = attempts,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                });
                return 0;
            }

            if (classifiedAsLocked &&
                acquired &&
                postReleaseControlAcquired &&
                attempts is >= 2 and <= MaximumExpectedRetryAttempts &&
                stopwatch.Elapsed >= TimeSpan.FromMilliseconds(400) &&
                retryException is null)
            {
                const string marker = "NO_BUG_1087";
                Console.WriteLine(marker);
                host.SendResult(false, marker, new
                {
                    NativeCode = nativeCode,
                    Attempts = attempts,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                });
                return FixedOutcomeExitCode;
            }

            throw new InvalidOperationException(
                "The classifier and retry observations disagreed; refusing to label an incomplete or bypassed change as a fix.");
        }
        finally
        {
            File.WriteAllText(paths.ContenderDone, "done");
        }
    }

    [SupportedOSPlatform("linux")]
    private static IOException CaptureCollision(FileStream stream)
    {
        try
        {
            stream.Lock(LockOffset, LockLength);
            stream.Unlock(LockOffset, LockLength);
            throw new InvalidOperationException(
                "The contender acquired the supposedly owned range; the OS lock control is invalid.");
        }
        catch (IOException ex)
        {
            return ex;
        }
    }

    private static int GetNativeCode(IOException exception) => exception.HResult & 0xFFFF;

    private static void WaitForFile(string path, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();

        while (!File.Exists(path))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for coordination file '{path}'.");
            }

            Thread.Sleep(20);
        }
    }

    private sealed record ScenarioPaths(
        string Root,
        string Database,
        string Ready,
        string ReleaseRequest,
        string Released,
        string ContenderDone)
    {
        public static ScenarioPaths Create(string? sharedDatabaseRoot)
        {
            var root = string.IsNullOrWhiteSpace(sharedDatabaseRoot)
                ? Path.Combine(AppContext.BaseDirectory, "issue1087")
                : sharedDatabaseRoot;

            return new ScenarioPaths(
                root,
                Path.Combine(root, "lock-target.db"),
                Path.Combine(root, "owner-ready"),
                Path.Combine(root, "release-request"),
                Path.Combine(root, "owner-released"),
                Path.Combine(root, "contender-done"));
        }
    }
}
