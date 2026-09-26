using System;
using System.IO;

namespace LiteDB.Client.Shared
{
    /// <summary>Writer participation on every target, including runtimes without the mapped fast path.</summary>
    internal static class SharedCoordinationFallback
    {
        internal const long Magic = 0x314452485342444c;
        internal static bool IsOwned(FileStream file)
        {
            if (file.Length < 8) return false;
            var bytes = new byte[8];
            return file.Read(bytes, 0, bytes.Length) == bytes.Length && BitConverter.ToInt64(bytes, 0) == Magic;
        }

        internal static string PagePath(string filename) => filename + "-shared-state";
        internal static string DisabledPath(string filename) => filename + "-shared-disabled";
        internal static string LivePath(string filename) => filename + "-shared-live";

        /// <summary>
        /// Caller owns the database mutex. A page cannot first appear until that ownership
        /// ends. Unexpected access failures propagate: File.Exists would incorrectly turn
        /// an unreadable authority into proof that no fast reader can exist.
        /// </summary>
        internal static void RevokeIfPresent(string filename)
        {
            if (!SharedCoordinationRevocation.IsRevoked(PagePath(filename))) return;
            try { File.GetAttributes(PagePath(filename)); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            Revoke(filename);
        }

        internal static void Revoke(string filename)
        {
            if (File.Exists(DisabledPath(filename))) return;
            using (var marker = new FileStream(DisabledPath(filename), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = BitConverter.GetBytes(Magic);
                marker.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>Caller owns the database mutex and has closed its participation handle.</summary>
        internal static void TryRetire(string filename)
        {
            try
            {
                using (var live = new FileStream(LivePath(filename), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (!IsOwned(live)) return;
                    using (var page = new FileStream(PagePath(filename), FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (!IsOwned(page)) return;
                    File.Delete(PagePath(filename));
                    if (File.Exists(DisabledPath(filename)))
                    {
                        using (var marker = new FileStream(DisabledPath(filename), FileMode.Open, FileAccess.Read, FileShare.Read))
                            if (!IsOwned(marker)) return;
                        File.Delete(DisabledPath(filename));
                    }
                }
                File.Delete(LivePath(filename));
            }
            catch (IOException) { }
            catch (System.UnauthorizedAccessException) { }
        }
    }
}
