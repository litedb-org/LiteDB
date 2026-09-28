using System;
using System.IO;

namespace LiteDB
{
    /// <summary>
    /// Database admission failed because another connection is incompatible or the
    /// mode guard is unavailable. Inspect InnerException for the filesystem cause.
    /// </summary>
    public sealed class DatabaseAdmissionException : IOException
    {
        internal DatabaseAdmissionException(string filename, Exception inner)
            : base("Cannot safely admit database access to '" + filename +
                "': could not acquire the OS-native database admission lock. " +
                "An incompatible connection or unsupported locking environment may prevent access. Cause: " + inner.Message, inner)
        {
            // Preserve the native cause so bounded sharing-violation retries can
            // distinguish contention from permissions, malformed identity, or I/O.
            HResult = inner.HResult;
        }
    }
}
