#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB.Engine;

namespace LiteDB.Client.Shared
{
    // Coordination is disposable, never a source of database/WAL recovery data.
    // Each removal is independently retryable, including directory-entry replay
    // after power loss. No persistent completion marker is needed or trusted.
    internal static class SharedOfflineUpgrade
    {
        internal static void Run(EngineSettings settings, NativeAdmission admission)
        {
            var filename = settings.Filename;
            var paths = new[] { "-shared-state", "-shared-disabled", "-shared-live" }
                .Select(s => filename + s).Where(FileHelper.ExistsOrThrow).ToList();
            var readers = filename + "-readers";
            var hasReaders = Directory.Exists(readers);
            var shm = filename + "-shm";
            var abi1 = false;
            if (FileHelper.ExistsOrThrow(shm))
            {
                RequireRegular(shm);
                using var file = new FileStream(shm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                abi1 = SharedCoordinationProtocol.Inspect(file, true, filename) != null;
            }
            if (paths.Count == 0 && !hasReaders && !abi1) return;
            if (settings.ReadOnly || settings.ReadOnlyStorage)
                throw new IOException("Offline coordination upgrade requires a writable open with all processes closed.");
            if (FileHelper.ExistsOrThrow(shm) && !abi1)
            {
                using var file = new FileStream(shm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var magic = new byte[8];
                var count = file.Read(magic, 0, magic.Length);
                if (count != 0 && !magic.All(b => b == 0) &&
                    !magic.SequenceEqual(System.Text.Encoding.ASCII.GetBytes("LDBSHM2\0")))
                    throw new IOException("Unknown legacy coordination remnant preserved: " + shm);
            }
            using var path = NativeAdmission.EnterPath(filename);
            using var exclusive = admission.ExclusiveOffline();
            // This also refuses participants that own DMS independently of admission.
            // No coordination file is created merely to prove quiescence.
            using (var dms = FileHelper.ExistsOrThrow(shm) ? new DatabaseFileLock(shm, false, false) : null)
            {
                dms?.Lock(SharedMemoryLayout.Dms, true);
                Validate(paths, filename);
                var leases = hasReaders ? ReaderFiles(readers) : Array.Empty<string>();
                // Preflight ALL files before the first removal. Unknown input is
                // preserved in place and reported; neither recursive delete nor glob
                // cleanup is allowed here.
                SharedCoordinationFile.Observe(filename, "offline-validated");
                foreach (var candidate in paths.Where(p => !p.EndsWith("-shared-live", StringComparison.Ordinal)))
                    Retire(candidate);
                foreach (var candidate in leases) Retire(candidate);
                if (hasReaders)
                {
                    Directory.Delete(readers, false);
                    SharedCoordinationFile.Observe(filename, "offline-readers-retired");
                }
                foreach (var candidate in paths.Where(p => p.EndsWith("-shared-live", StringComparison.Ordinal)))
                    Retire(candidate);
            }
            // Admission stays exclusive across close/unlink. An ABI-2 envelope is
            // never removed; only a fully recognised ABI-1 status page is retired.
            if (abi1) Retire(shm);
            SharedCoordinationFile.Observe(filename, "offline-complete");
        }

        private static void Validate(IEnumerable<string> paths, string filename)
        {
            foreach (var path in paths)
            {
                RequireRegular(path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var valid = path.EndsWith("-shared-disabled", StringComparison.Ordinal)
                    ? SharedCoordinationProtocol.IsMarker(file)
                    : SharedCoordinationProtocol.Inspect(file, path.EndsWith("-shared-state", StringComparison.Ordinal), filename) != null;
                if (!valid) throw new IOException("Unknown legacy coordination remnant preserved: " + path);
            }
        }

        private static string[] ReaderFiles(string directory)
        {
            RequireRegular(directory);
            var paths = Directory.GetFileSystemEntries(directory).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (var path in paths)
            {
                RequireRegular(path);
                var name = Path.GetFileNameWithoutExtension(path);
                var extension = Path.GetExtension(path);
                var slots = name.StartsWith("slots-", StringComparison.Ordinal) && Guid.TryParseExact(name.Substring(6), "N", out _);
                var separator = name.IndexOf('-');
                var legacy = separator > 0 && int.TryParse(name.Substring(0, separator), out var version) && version >= 0 &&
                    Guid.TryParseExact(name.Substring(separator + 1), "N", out _);
                using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (!((slots && (extension == ".lease" || extension == ".slots")) || (legacy && extension == ".lease")) ||
                    (extension == ".lease" && file.Length != 0) ||
                    (extension == ".slots" && (file.Length % 8 != 0 || file.Length > 65536 * 8)))
                    throw new IOException("Unknown legacy reader remnant preserved: " + path);
            }
            return paths;
        }

        private static void RequireRegular(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Coordination aliases are not upgraded: " + path);
        }
        private static void Retire(string path)
        {
            File.Delete(path);
            SharedCoordinationFile.Observe(path, "offline-retired");
        }
    }
}
#endif
