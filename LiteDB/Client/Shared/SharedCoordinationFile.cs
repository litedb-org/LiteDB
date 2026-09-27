#if NET8_0_OR_GREATER
using System;
using System.Diagnostics;
using System.IO;

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
            var temporary = Path.Combine(Path.GetDirectoryName(path), ".litedb-control-" + Guid.NewGuid().ToString("N"));
            var created = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    Observe(path, "created");
                    file.Write(content, 0, content.Length);
                    Observe(path, "written");
                    file.Flush(true);
                    Observe(path, "flushed");
                }
                File.Move(temporary, path);
                Observe(path, "published");
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

        [Conditional("DEBUG"), Conditional("TESTING")]
        private static void Observe(string path, string stage)
        {
#if DEBUG || TESTING
            CreationStage?.Invoke(path, stage);
#endif
        }
    }
}
#endif
