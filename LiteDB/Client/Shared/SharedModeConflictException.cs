using System;
using System.IO;

namespace LiteDB
{
    /// <summary>
    /// Database admission failed because another connection is incompatible or the
    /// mode guard is unavailable. Inspect InnerException for the filesystem cause.
    /// </summary>
    public sealed class SharedModeConflictException : IOException
    {
        internal SharedModeConflictException(string filename, Exception inner)
            : base("Cannot safely admit database access to '" + filename +
                "': conflicting Direct/Shared access, Shared mutex identity, or unavailable mode guard. " +
                "Close incompatible connections, use one database path and mutex strategy, and ensure " +
                "the directory permits creating and opening the '-shared-mode' file. Cause: " + inner.Message, inner) { }
    }
}
