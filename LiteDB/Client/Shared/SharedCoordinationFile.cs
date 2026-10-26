using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    /// <summary>Publish complete ephemeral control files under the database mutex.</summary>
    internal static class SharedCoordinationFile
    {
#if DEBUG || TESTING
        [ThreadStatic]
        internal static Action<string, string> CreationStage;
#endif

        internal static void Publish(string path, byte[] content)
        {
            // Same-directory rename publishes complete contents without replacing
            // an unknown destination. A killed process can leave an ignored temp
            // file, never a partially initialized authority at the final path.
            // At most 21 characters: even a one-character database name at the
            // existing 239-character Windows limit keeps this path below MAX_PATH.
            SharedCoordinationTemporaryFiles.Cleanup(path);
            var temporary = SharedCoordinationTemporaryFiles.CreateName(path);
            var created = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Delete))
                {
                    created = true;
                    Observe(path, "created");
                    file.Write(content, 0, content.Length);
                    Observe(path, "written");
                    file.Flush(true);
                    Observe(path, "flushed");
                    // Keep the handle through rename so cleanup can prove no publisher
                    // survives, including in the flushed-to-published interval.
                    RetrySharingViolation(() =>
                    {
                        Observe(path, "publishing");
                        File.Move(temporary, path);
                        return true;
                    });
                    Observe(path, "published");
                }
            }
            finally
            {
                if (created)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        // Retry only native Windows sharing/lock violations, never unsupported or
        // malformed authorities, access denial, existing destinations or generic I/O.
        internal static T RetrySharingViolation<T>(Func<T> operation)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return operation(); }
                catch (IOException error) when (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33) && attempt < 4)
                { Thread.Sleep(10); }
            }
        }

        [Conditional("DEBUG"), Conditional("TESTING")]
        internal static void Observe(string path, string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint(stage);
            CreationStage?.Invoke(path, stage);
#endif
        }
    }
}
