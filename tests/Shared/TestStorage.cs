using System;
using System.Diagnostics;
using System.IO;

namespace LiteDB.Tests
{
    // Initialize before test classes (and their field initializers) are constructed.
    internal static class TestStorage
    {
        private static readonly Lazy<string> DirectoryPath = new Lazy<string>(Initialize);

        internal static string Root => DirectoryPath.Value;

        internal static string SelectRoot(string mode, string configuredRoot, bool ci, string systemTemp, string ramRoot)
        {
            if (!string.IsNullOrEmpty(mode) && mode != "ram" && mode != "disk")
                throw new ArgumentException("LITEDB_TEST_STORAGE must be 'ram' or 'disk'.");

            if (mode == "disk" || (string.IsNullOrEmpty(mode) && ci)) return systemTemp;
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                if (!Path.IsPathRooted(configuredRoot))
                    throw new ArgumentException("LITEDB_TEST_TEMP_ROOT must be an absolute RAM-disk path.");
                return configuredRoot;
            }
            if (ramRoot != null) return ramRoot;

            throw new InvalidOperationException("No RAM filesystem found. Set LITEDB_TEST_TEMP_ROOT to a mounted RAM disk, " +
                "or explicitly opt into disk writes with LITEDB_TEST_STORAGE=disk. See docs/testing-storage.md.");
        }

        private static string Initialize()
        {
            var mode = Environment.GetEnvironmentVariable("LITEDB_TEST_STORAGE");
            var ci = IsTrue(Environment.GetEnvironmentVariable("CI")) ||
                IsTrue(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
            var systemTemp = Path.GetTempPath();
            var disk = mode == "disk" || (string.IsNullOrEmpty(mode) && ci);
            var configuredRoot = Environment.GetEnvironmentVariable("LITEDB_TEST_TEMP_ROOT");
            var root = SelectRoot(mode, configuredRoot, ci, systemTemp,
                disk || !string.IsNullOrWhiteSpace(configuredRoot) ? null : FindRamRoot());
            // Preserve CI's original volume and diagnostic paths exactly.
            if (disk) return root;

            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("RAM-disk root does not exist: " + root);
            using var process = Process.GetCurrentProcess();
            var directory = Path.Combine(root, "litedb-tests-" + process.Id + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            // Also covers engine sort spills, :temp: databases and inherited child-process temp files.
            foreach (var variable in new[] { "TMPDIR", "TEMP", "TMP" })
                Environment.SetEnvironmentVariable(variable, directory);
            EventHandler cleanup = (_, __) =>
            {
                try { Directory.Delete(directory, true); }
                catch (IOException) { } // A killed worker may still own a file; leave it for inspection.
                catch (UnauthorizedAccessException) { }
            };
            AppDomain.CurrentDomain.ProcessExit += cleanup;
            AppDomain.CurrentDomain.DomainUnload += cleanup;
            return directory;
        }

        private static bool IsTrue(string value) => value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

        private static string FindRamRoot()
        {
            if (!Directory.Exists("/dev/shm")) return null;
            // .NET 8 names the tmpfs filesystem magic "udev"; newer runtimes
            // report "tmpfs". Both identify memory-backed storage here.
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.Name.TrimEnd('/') != "/dev/shm") continue;
                var format = drive.DriveFormat;
                if (format == "tmpfs" || format == "ramfs" || format == "udev") return "/dev/shm";
            }
            return null;
        }
    }
}
