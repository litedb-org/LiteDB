using System.IO;

namespace LiteDB.Engine
{
    /// <summary>
    /// A write refused because the database is read-only, before any mutation. It remains an
    /// <see cref="IOException"/> for existing callers; a transaction handle stays active.
    /// </summary>
    internal sealed class ReadOnlyRefusalException : IOException
    {
        internal ReadOnlyRefusalException(string message) : base(message) { }
    }
}
