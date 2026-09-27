namespace LiteDB.Engine
{
#if DEBUG || TESTING
    /// <summary>
    /// Test storage boundary that distinguishes an OS-cache flush from a durable flush.
    /// </summary>
    internal interface IDurableStream
    {
        void FlushToDisk();
    }
#endif
}
