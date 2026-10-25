namespace LiteDB.Client.Shared
{
    // ABI 2 bootstrap constants. These offsets never depend on configured capacity.
    // Lock bytes sit beyond mapped payload, including the largest supported map.
    internal static class SharedMemoryLayout
    {
        internal const int Major = 2;
        internal const int Page = 4096;
        internal const int EnvelopeBytes = 256;
        internal const int Control = 2 * Page;
        internal const int Blocks = 4 * Page;
        internal const int ReaderOffset = 256;
        internal const int RequestOffset = 64;
        internal const int WakeOffset = 128;
        internal const int CoordinatorOffset = Control + 512;
        internal const int CoordinatorBytes = 512;
        internal const int DefaultProcesses = 256;
        internal const int DefaultGenerations = 256;
        internal const long Dms = 0x40000000;
        internal const long Write = Dms + 1;
        internal const long SlotLocks = Dms + 64;
    }
}
