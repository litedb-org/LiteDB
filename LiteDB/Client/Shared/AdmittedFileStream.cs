using System.IO;
using System.Runtime.InteropServices;

namespace LiteDB.Client.Shared
{
    internal static class AdmittedFileStream
    {
        internal static FileStream Open(string filename, FileMode mode, FileAccess access,
            FileShare share, int bufferSize, FileOptions options)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new FileStream(filename, mode, access, share, bufferSize, options);

            // Darwin's flock and OFD locks share one lock table. FileStream's
            // automatic whole-file flock would conflict with our admission range,
            // or make a Shared stream look like every incompatible family. The
            // engine already holds admission; open its data descriptor directly.
            var handle = DatabaseFileIdentity.Open(filename, access == FileAccess.Read, mode == FileMode.OpenOrCreate);
            try { return new FileStream(handle, access, bufferSize); }
            catch { handle.Dispose(); throw; }
        }
    }
}
