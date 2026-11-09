#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using LiteDB.Client.Shared;

namespace LiteDB.Tests
{
    /// <summary>Bounded accommodation for Windows sharing locks after native process death.</summary>
    internal static class WindowsFileRelease
    {
        internal static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        internal static void Retry(Action action, int timeoutMilliseconds = 3000)
        {
            var elapsed = Stopwatch.StartNew();
            ExceptionDispatchInfo? firstFailure = null;
            while (true)
            {
                try
                {
                    action();
                    return;
                }
                catch (IOException ex) when (IsWindows && IsSharingViolation(ex))
                {
                    if (firstFailure == null) firstFailure = ExceptionDispatchInfo.Capture(ex);
                    var remaining = timeoutMilliseconds - elapsed.ElapsedMilliseconds;
                    if (remaining <= 0) firstFailure.Throw();
                    Thread.Sleep((int)Math.Min(25, remaining));
                }
            }
        }

        internal static bool IsSharingViolation(IOException error) =>
            error.HResult == unchecked((int)0x80070020) || error.HResult == unchecked((int)0x80070021);

        // Open, never create/delete/truncate: crash evidence must remain intact for recovery assertions.
        // A missing path is already released (including DeleteOnClose reader leases).
        internal static void WaitForFiles(IEnumerable<string> paths, int timeoutMilliseconds = 5000)
        {
            if (!IsWindows) return;
            Retry(() =>
            {
                foreach (var path in paths)
                {
                    try
                    {
                        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }, timeoutMilliseconds);
        }

        // Call only after every intentional participant has closed. Process exit and
        // SafeHandle.Close do not guarantee synchronous Windows byte-range release.
        // Probe the actual range without opening/recovering/mutating database storage.
        internal static void WaitForNativeAdmission(string filename, int timeoutMilliseconds = 5000)
        {
            if (!IsWindows) return;
            var elapsed = Stopwatch.StartNew();
            var attempts = 0;
            using var probe = new DatabaseFileLock(filename, true, false, qualify: false);
            while (true)
            {
                attempts++;
                if (probe.TryLock(DatabaseFileLock.Admission, exclusive: true))
                {
                    probe.Unlock(DatabaseFileLock.Admission);
                    Console.WriteLine("Windows native admission released: attempts=" + attempts +
                        " elapsedMs=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3",
                            System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
                var remaining = timeoutMilliseconds - elapsed.ElapsedMilliseconds;
                if (remaining <= 0)
                    throw new IOException("Windows native admission remained locked after " +
                        elapsed.ElapsedMilliseconds + " ms: " + filename);
                Thread.Sleep((int)Math.Min(1, remaining));
            }
        }

        internal static void DeleteDirectory(string path) => Retry(() => Directory.Delete(path, true));
    }
}
