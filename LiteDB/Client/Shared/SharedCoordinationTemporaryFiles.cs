using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LiteDB.Client.Shared
{
    /// <summary>Only attributed, recognizable and unheld control temporaries are removable.</summary>
    internal static class SharedCoordinationTemporaryFiles
    {
        private static string Prefix(string path)
        {
            var normalized = Path.GetFullPath(path);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) normalized = normalized.ToLowerInvariant();
            using (var hash = SHA256.Create())
                return ".ldb-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized)), 0, 4)
                    .Replace("-", "").ToLowerInvariant() + "-";
        }

        internal static string CreateName(string path) => Path.Combine(Path.GetDirectoryName(path),
            Prefix(path) + Guid.NewGuid().ToString("N").Substring(0, 7)); // 21 characters, within the existing path budget.

        internal static void CleanupDatabase(string filename)
        {
            Cleanup(SharedCoordinationFallback.LivePath(filename));
            Cleanup(SharedCoordinationFallback.PagePath(filename));
            Cleanup(SharedCoordinationFallback.DisabledPath(filename));
        }

        // Caller owns the database mutex. The short tag is a namespace filter, never
        // an authority identity: complete headers still have to match the database.
        internal static void Cleanup(string destination)
        {
            var suffix = destination.EndsWith("-shared-live", StringComparison.Ordinal) ? "-shared-live" :
                destination.EndsWith("-shared-state", StringComparison.Ordinal) ? "-shared-state" :
                destination.EndsWith("-shared-disabled", StringComparison.Ordinal) ? "-shared-disabled" : null;
            if (suffix == null) return;
            var filename = destination.Substring(0, destination.Length - suffix.Length);
            var prefix = Prefix(destination);
            try
            {
                foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(destination), prefix + "???????"))
                {
                    var name = Path.GetFileName(path);
                    if (name.Length != 21 || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var hexadecimal = true;
                    for (var i = prefix.Length; i < name.Length; i++)
                        hexadecimal &= (name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f');
                    if (!hexadecimal) continue;
                    try
                    {
                        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            if (file.Length != 0 && !(suffix == "-shared-disabled"
                                ? SharedCoordinationProtocol.IsMarker(file)
                                : SharedCoordinationProtocol.Inspect(file, suffix == "-shared-state", filename) != null)) continue;
                        }
                        // Publishers keep a sharing lock through rename. Even a tag
                        // collision cannot remove another database's live publisher.
                        // DeleteOnClose avoids a close/unlink gap with a new creator.
                        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1,
                            FileOptions.DeleteOnClose)) { }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
