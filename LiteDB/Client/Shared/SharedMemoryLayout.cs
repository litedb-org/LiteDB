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
        internal const int CommittedVersion = Control + 64;
        internal const int StructuralEpoch = Control + 128;
        internal const int ReuseEpoch = Control + 192;
        internal const int ResetEpoch = Control + 256;
        internal const int RecoveryOwner = Control + 320;
        internal const int WriterTenureEpoch = Control + 384;
        internal const int WriterOwner = Control + 392;
        internal const int FastAdmissionDisabled = Control + 448;
        internal const int WriterWake = Control + 1024;
        internal const int RequestSequence = Control + 1032;
        internal const int TenureRequest = 80;
        internal const int RequestExpiry = 88;
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
