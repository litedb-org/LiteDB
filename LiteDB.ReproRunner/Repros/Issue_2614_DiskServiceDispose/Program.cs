using System.Runtime.InteropServices;
using LiteDB;
using LiteDB.ReproRunner.Shared;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2614_DiskServiceDispose;

internal static class Program
{
    private const ulong FileSizeLimitBytes = 4096;
    private const string DatabaseFileName = "issue2614-disk.db";

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
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                const string message = "This repro requires a Unix-like environment to adjust RLIMIT_FSIZE.";
                host.SendLog(message, ReproHostLogLevel.Error);
                host.SendResult(false, message);
                host.SendLifecycle("completed", new { Success = false });
                return 1;
            }

            Run(host, context);

            host.SendResult(true, "DiskService left the database file locked after initialization failure.");
            host.SendLifecycle("completed", new { Success = true });
            return 0;
        }
        catch (Exception ex)
        {
            host.SendLog($"Reproduction failed: {ex}", ReproHostLogLevel.Error);
            host.SendResult(false, "Reproduction failed unexpectedly.", new { Exception = ex.ToString() });
            host.SendLifecycle("completed", new { Success = false });
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Run(ReproHostClient host, ReproContext context)
    {
        NativeMethods.IgnoreSigxfsz();

        var rootDirectory = string.IsNullOrWhiteSpace(context.SharedDatabaseRoot)
            ? Path.Combine(AppContext.BaseDirectory, "issue2614")
            : context.SharedDatabaseRoot;

        Directory.CreateDirectory(rootDirectory);

        var databasePath = Path.Combine(rootDirectory, DatabaseFileName);

        if (File.Exists(databasePath))
        {
            host.SendLog($"Removing pre-existing database file: {databasePath}");
            File.Delete(databasePath);
        }

        var limitScope = FileSizeLimitScope.Apply(host, FileSizeLimitBytes);

        var connectionString = new ConnectionString
        {
            Filename = databasePath,
            Connection = ConnectionType.Direct
        };

        host.SendLog($"Attempting to create database at {databasePath}");

        Exception? observedException = null;

        try
        {
            try
            {
                using var database = new LiteDatabase(connectionString);
                throw new InvalidOperationException("LiteDatabase constructor succeeded unexpectedly.");
            }
            catch (Exception ex)
            {
                observedException = ex;
                host.SendLog($"Observed exception while opening database: {ex.GetType().FullName}: {ex.Message}");
            }

            if (observedException is null)
            {
                throw new InvalidOperationException("No exception observed while creating the database.");
            }
        }
        finally
        {
            limitScope.Dispose();
        }

        host.SendLog("Restored original file size limit. Checking for lingering file handles...");

        var lockObserved = false;

        try
        {
            using var stream = File.Open(databasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            host.SendLog("Successfully reopened the database file with exclusive access.", ReproHostLogLevel.Error);
        }
        catch (IOException ioException)
        {
            lockObserved = true;
            host.SendLog($"Failed to reopen database file due to lingering handle: {ioException.Message}", ReproHostLogLevel.Information);
        }

        if (!lockObserved)
        {
            throw new InvalidOperationException("Database file reopened successfully; the repro did not observe the lock.");
        }

        try
        {
            host.SendLog("Attempting to delete the locked database file.");
            File.Delete(databasePath);
        }
        catch (Exception deleteException)
        {
            host.SendLog($"Expected failure deleting locked file: {deleteException.Message}");
        }
    }

    private sealed class FileSizeLimitScope : IDisposable
    {
        private readonly NativeMethods.RLimit _original;
        private bool _restored;

        private FileSizeLimitScope(NativeMethods.RLimit original)
        {
            _original = original;
        }

        public static FileSizeLimitScope Apply(ReproHostClient host, ulong requestedLimit)
        {
            var original = NativeMethods.GetFileSizeLimit();
            host.SendLog($"Original RLIMIT_FSIZE: cur={original.rlim_cur}, max={original.rlim_max}");

            var newLimit = original;
            var effectiveLimit = Math.Min(original.rlim_max, requestedLimit);

            if (effectiveLimit == 0)
            {
                throw new InvalidOperationException($"Unable to set RLIMIT_FSIZE to zero. Original max={original.rlim_max}.");
            }

            newLimit.rlim_cur = effectiveLimit;
            NativeMethods.SetFileSizeLimit(newLimit);

            host.SendLog($"Applied RLIMIT_FSIZE cur={newLimit.rlim_cur}");

            return new FileSizeLimitScope(original);
        }

        public void Dispose()
        {
            if (_restored)
            {
                return;
            }

            NativeMethods.SetFileSizeLimit(_original);
            _restored = true;
        }
    }

    private static class NativeMethods
    {
        private const int RLimitFileSize = 1;
        private const int Sigxfsz = 25;
        private static readonly IntPtr SigIgn = (IntPtr)1;
        private static readonly IntPtr SigErr = (IntPtr)(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RLimit
        {
            public ulong rlim_cur;
            public ulong rlim_max;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int getrlimit(int resource, out RLimit rlim);

        [DllImport("libc", SetLastError = true)]
        private static extern int setrlimit(int resource, ref RLimit rlim);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr signal(int signum, IntPtr handler);

        public static void IgnoreSigxfsz()
        {
            var previous = signal(Sigxfsz, SigIgn);

            if (previous == SigErr)
            {
                throw new InvalidOperationException($"signal failed (errno={Marshal.GetLastWin32Error()}).");
            }
        }

        public static RLimit GetFileSizeLimit()
        {
            ThrowOnError(getrlimit(RLimitFileSize, out var limit));
            return limit;
        }

        public static void SetFileSizeLimit(RLimit limit)
        {
            ThrowOnError(setrlimit(RLimitFileSize, ref limit));
        }

        private static void ThrowOnError(int result)
        {
            if (result != 0)
            {
                throw new InvalidOperationException($"Native call failed (errno={Marshal.GetLastWin32Error()}).");
            }
        }
    }
}
