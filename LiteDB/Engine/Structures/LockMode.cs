namespace LiteDB.Engine
{
    /// <summary>
    /// Represents a snapshot lock mode
    /// </summary>
    internal enum LockMode
    {
        /// <summary>
        /// Read only snap with read lock
        /// </summary>
        Read,

        /// <summary>
        /// Read/Write snapshot with reserved lock
        /// </summary>
        Write
    }
}