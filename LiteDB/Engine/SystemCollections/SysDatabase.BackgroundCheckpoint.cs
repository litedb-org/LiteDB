namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        // Read-only scalar snapshots. Never request/join maintenance or invoke
        // worker hooks while a system-collection query owns storage locks.
        private BsonDocument BackgroundCheckpointDiagnostics()
        {
            var worker = _disk.AutomaticCheckpoint;
            var admission = _disk.WriteAdmission;
            var metrics = _disk.CheckpointMetrics;
            return new BsonDocument
            {
                ["enabled"] = worker != null,
                ["state"] = worker?.State.ToString() ?? "Disabled",
                ["logicalBacklogPages"] = _disk.GetFileLength(FileOrigin.Log) / Constants.PAGE_SIZE,
                ["peakLogicalPages"] = metrics == null ? BsonValue.Null : new BsonValue(metrics.PeakLogicalPages),
                ["peakPhysicalWalBytes"] = metrics == null ? BsonValue.Null : new BsonValue(metrics.PeakPhysicalBytes),
                ["admissionClosed"] = admission?.UnderPressure ?? false,
                ["writers"] = admission?.Writers ?? 0,
                ["admissionWaitCount"] = admission?.WaitCount ?? 0,
                ["admissionWaitMilliseconds"] = admission?.WaitMilliseconds ?? 0,
                ["requestCount"] = metrics?.Requests ?? 0,
                ["passCount"] = metrics?.Passes ?? 0,
                ["activeMilliseconds"] = metrics?.ActiveMilliseconds ?? 0,
                ["lastFailure"] = worker?.LastFailure == null ? BsonValue.Null : new BsonValue(worker.LastFailure)
            };
        }
    }
}
