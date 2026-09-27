using System;
using System.IO;

namespace LiteDB.Client.Shared
{
    /// <summary>Authority creation and retirement, always under the database mutex.</summary>
    internal static class SharedCoordinationFiles
    {
        internal static byte[] Open(string filename, out FileStream participation, out FileStream page)
        {
            participation = null;
            page = null;
            SharedCoordinationPolicy.RequireFileLocking();
            TryRetire(filename);
            var livePath = SharedCoordinationFallback.LivePath(filename);
            var pagePath = SharedCoordinationFallback.PagePath(filename);
            if (!SharedCoordinationRevocation.ExistsOrUnknown(livePath))
            {
                // Never manufacture liveness for an unpaired or unknown existing authority.
                if (SharedCoordinationRevocation.ExistsOrUnknown(pagePath) ||
                    SharedCoordinationRevocation.ExistsOrUnknown(SharedCoordinationFallback.DisabledPath(filename)))
                    throw new IOException("Unpaired or unknown Shared coordination files: " + filename);
                SharedCoordinationFile.Publish(livePath, SharedCoordinationProtocol.CreateParticipation(filename));
            }
            participation = new FileStream(livePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var liveHeader = SharedCoordinationProtocol.Inspect(participation, page: false, filename);
            if (liveHeader == null || liveHeader.Legacy)
                throw new IOException("Unsupported Shared participation protocol or database identity: " + livePath);
            if (!SharedCoordinationRevocation.ExistsOrUnknown(pagePath))
                SharedCoordinationFile.Publish(pagePath, SharedCoordinationProtocol.CreatePage(liveHeader));
            page = new FileStream(pagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var pageHeader = SharedCoordinationProtocol.Inspect(page, page: true, filename);
            if (pageHeader == null || pageHeader.Legacy || !liveHeader.SameAuthority(pageHeader))
                throw new IOException("Unsupported Shared page protocol or authority identity: " + pagePath);
            return pageHeader.Bytes;
        }

        /// <summary>Retire only understood files after exclusive participation proves all old users gone.</summary>
        internal static void TryRetire(string filename)
        {
            SharedCoordinationPolicy.RequireFileLocking();
            var livePath = SharedCoordinationFallback.LivePath(filename);
            var pagePath = SharedCoordinationFallback.PagePath(filename);
            var markerPath = SharedCoordinationFallback.DisabledPath(filename);
            try
            {
                FileStream live;
                try { live = new FileStream(livePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (FileNotFoundException)
                {
                    // A marker alone cannot name a mapped authority. An orphan page can;
                    // without its liveness proof, preserve it and refuse mapped attachment.
                    if (!SharedCoordinationRevocation.ExistsOrUnknown(pagePath) && CanRetireMarker(markerPath, out var marker))
                    {
                        SharedCoordinationTemporaryFiles.CleanupDatabase(filename);
                        if (marker) Retire(markerPath);
                    }
                    return;
                }
                using (live)
                {
                    var liveHeader = SharedCoordinationProtocol.Inspect(live, page: false, filename);
                    if (liveHeader == null) return;
                    var pagePresent = false;
                    try
                    {
                        using (var page = new FileStream(pagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            pagePresent = true;
                            var header = SharedCoordinationProtocol.Inspect(page, page: true, filename);
                            if (header == null || !liveHeader.SameAuthority(header)) return;
                        }
                    }
                    catch (FileNotFoundException) { }
                    // Validate the entire set before removing anything, including an unknown marker.
                    if (!CanRetireMarker(markerPath, out var markerPresent)) return;
                    SharedCoordinationTemporaryFiles.CleanupDatabase(filename);
                    if (pagePresent) Retire(pagePath);
                    if (markerPresent) Retire(markerPath);
                }
                // The database mutex excludes a new participant in the close/delete gap.
                Retire(livePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool CanRetireMarker(string path, out bool present)
        {
            present = false;
            try
            {
                using (var marker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    present = true;
                    return SharedCoordinationProtocol.IsMarker(marker);
                }
            }
            catch (FileNotFoundException) { return true; }
        }

        private static void Retire(string path)
        {
            File.Delete(path);
            SharedCoordinationFile.Observe(path, "retired");
        }
    }
}
