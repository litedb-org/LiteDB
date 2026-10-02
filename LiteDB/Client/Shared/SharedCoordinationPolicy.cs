using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    internal static class SharedCoordinationPolicy
    {
        internal const string DisableMappedSwitch = "LiteDB.DisableSharedMappedReads";
        internal const string DisableMappedEnvironment = "LITEDB_DISABLE_SHARED_MAPPED_READS";

        // Configure before creating connections; existing authorities retain their contract.
        internal static bool MappedReadsDisabled => Enabled(DisableMappedSwitch, DisableMappedEnvironment);

        internal static void RequireFileLocking()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                ((AppContext.TryGetSwitch("System.IO.DisableFileLocking", out var disabled) && disabled) ||
                 EnvironmentEnabled("DOTNET_SYSTEM_IO_DISABLEFILELOCKING")))
            {
                Reachability.Sometimes("refusal:shared-file-locking-disabled");
                throw new PlatformNotSupportedException("Shared readers require OS file-sharing locks. " +
                    "Remove System.IO.DisableFileLocking / DOTNET_SYSTEM_IO_DISABLEFILELOCKING before process startup.");
            }
        }

#if NET8_0_OR_GREATER
        // Shared by runtime qualification and the mapped-test directory fixture.
        internal static string VolumeFailure(string filename)
        {
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives().Where(d => filename.StartsWith(
                d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault();
            return drive != null && drive.DriveType == DriveType.Fixed &&
                new[] { "ext2", "ext3", "ext4", "xfs", "btrfs", "NTFS", "ReFS", "apfs" }.Contains(drive.DriveFormat)
                ? null : "volume: " + drive?.Name + "/" + drive?.DriveType + "/" + drive?.DriveFormat;
        }
#endif

        // Our opt-out gives an explicit switch precedence. Runtime file-locking
        // precedence differs across .NET versions, so reject either disabling knob.
        private static bool Enabled(string name, string environment)
        {
            if (AppContext.TryGetSwitch(name, out var value)) return value;
            return EnvironmentEnabled(environment);
        }

        private static bool EnvironmentEnabled(string environment)
        {
            var text = Environment.GetEnvironmentVariable(environment);
            return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
