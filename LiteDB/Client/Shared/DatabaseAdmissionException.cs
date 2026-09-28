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
                "': could not acquire database admission using '" + filename + "-shared-mode'. " +
                "An incompatible connection or unavailable coordination storage may prevent access. Cause: " + inner.Message, inner)
        {
            // Preserve the native cause so bounded sharing-violation retries can
            // distinguish contention from permissions, malformed identity, or I/O.
            HResult = inner.HResult;
        }
    }
}
