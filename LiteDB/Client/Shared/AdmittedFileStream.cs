using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
            try { return new NamedStream(handle, filename, access, bufferSize); }
            catch { handle.Dispose(); throw; }
        }

        // FileStream.Name is not virtual in netstandard2.0. Preserve the path
        // explicitly for device-sync diagnostics on every supported target.
        internal static string GetName(FileStream stream) => stream is NamedStream named ? named.Filename : stream.Name;

        private sealed class NamedStream : FileStream
        {
            internal string Filename { get; }
            internal NamedStream(SafeFileHandle handle, string filename, FileAccess access, int bufferSize)
                : base(handle, access, bufferSize) { Filename = filename; }
#if NET8_0_OR_GREATER
            public override string Name => Filename;
#endif
        }
    }
}
