using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteDB.Client.Shared
{
    /// <summary>Writer participation on every target, including runtimes without the mapped fast path.</summary>
    internal static class SharedCoordinationFallback
    {
        internal const long Magic = SharedCoordinationProtocol.Magic;

        // Every participant uses this same conservative name policy. Keeping the
        // longest suffix within a 255-byte component and ordinary Windows paths
        // avoids requiring a revocation marker that some eligible participant
        // cannot name. Longer database paths retain the existing mutex protocol.
        internal static bool SupportsNames(string filename) =>
            Encoding.UTF8.GetByteCount(Path.GetFileName(filename)) <= 239 &&
            (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || filename.Length <= 239);

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
            if (!SupportsNames(filename) || !SharedCoordinationRevocation.ExistsOrUnknown(PagePath(filename))) return;
            try { File.GetAttributes(PagePath(filename)); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            Revoke(filename);
        }

        internal static void Revoke(string filename)
        {
            if (File.Exists(DisabledPath(filename))) return;
            SharedCoordinationFile.Publish(DisabledPath(filename), BitConverter.GetBytes(Magic));
            SharedCoordinationEvents.Log.Transition(filename, "revoked", "fallback writer");
        }

        /// <summary>Caller owns the database mutex and has closed its participation handle.</summary>
        internal static void TryRetire(string filename) => SharedCoordinationFiles.TryRetire(filename);
    }
}
