using System.Threading;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        // Null by default. An opted-in, process-owned Direct core installs these
        // after synchronous open prerequisites, before publishing its facades.
        internal CheckpointWriteAdmission WriteAdmission { get; set; }
        internal AutomaticCheckpointCoordinator AutomaticCheckpoint { get; set; }
        internal BackgroundCheckpointMetrics CheckpointMetrics => _checksums.Metrics;
        private readonly bool _encryptedWal;

        internal void InstallCheckpointMetrics()
        {
            _checksums.Metrics = new BackgroundCheckpointMetrics(
                ((ChecksummedWalFactory)_logFactory).GetPhysicalLength(_encryptedWal), _encryptedWal);
            _checksums.Metrics.ObserveLogical(this.GetFileLength(FileOrigin.Log));
        }

        internal void ValidateWriteAdmission() => _state.Validate();

        private long AppendLogPosition()
        {
            var position = Interlocked.Add(ref _logLength, PAGE_SIZE);
            this.PublishCheckpointTail();
            return position;
        }

        internal void PublishCheckpointTail()
        {
            var bytes = this.GetFileLength(FileOrigin.Log);
            _checksums.Metrics?.ObserveLogical(bytes);
            WriteAdmission?.PublishTail(bytes);
            AutomaticCheckpoint?.PublishTail(bytes);
        }

        internal void ConfigureCheckpointAdmission(int checkpoint)
        {
            var bytes = this.GetFileLength(FileOrigin.Log);
            WriteAdmission?.Configure(checkpoint, bytes);
            AutomaticCheckpoint?.Configure(checkpoint, bytes);
        }
    }
}
