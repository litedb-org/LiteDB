namespace LiteDB.Engine
{
    /// <summary>Last nonempty WAL recovery result, retained by a SharedEngine owner.</summary>
    internal sealed class WalRecoveryReport
    {
        internal long DiscardedBytes { get; }
        internal bool InvalidTail { get; }

        internal WalRecoveryReport(long discardedBytes, bool invalidTail)
        {
            DiscardedBytes = discardedBytes;
            InvalidTail = invalidTail;
        }
    }

    public partial class LiteEngine
    {
        private WalRecoveryReport _retainedRecoveryReport;

        internal WalRecoveryReport RecoveryReport
        {
            get => _disk.RecoveryReport ?? _retainedRecoveryReport;
            set => _retainedRecoveryReport = value;
        }
    }
}
