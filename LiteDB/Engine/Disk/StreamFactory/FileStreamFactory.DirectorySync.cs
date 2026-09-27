using System.IO;

namespace LiteDB.Engine
{
    internal partial class FileStreamFactory
    {
        /// <summary>
        /// On Unix, syncing the file alone does not persist a newly created WAL's directory
        /// entry: it must be durable before a commit or an overwrite depends on the WAL.
        /// Windows uses the preceding FlushFileBuffers call. Failures are
        /// <see cref="FileSyncException"/>s, classified like file syncs.
        /// </summary>
        internal void SyncDirectory() =>
            NativeFileSync.SyncDirectory(Path.GetDirectoryName(Path.GetFullPath(_filename)));
    }
}
