using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>
        /// Called before an open-time step changes a stream (recovery, repair, trimming,
        /// conversion). Storage that cannot be written is then opened read-only instead.
        /// </summary>
        private void RequireWritableStorage()
        {
            if (_readOnlyStorage) throw new ReadOnlyOpenRequiredException();
        }

        /// <summary>
        /// Remove incomplete trailing pages only after the data header was validated.
        /// </summary>
        internal void TrimTrailingPages()
        {
            if (_readOnly) return;
            if (_dataTrailingLength != 0 || (!ChecksumsEnabled && _logTrailingLength != 0)) this.RequireWritableStorage();

            this.TrimTrailingPage(_dataPool, _dataLength + PAGE_SIZE, ref _dataTrailingLength);
            if (!ChecksumsEnabled) this.TrimTrailingPage(_logPool, _logLength + PAGE_SIZE, ref _logTrailingLength);
        }

        private void TrimTrailingPage(StreamPool pool, long length, ref long trailingLength)
        {
            if (trailingLength == 0) return;

            using var structural = new StructuralScope(_signals);
            var stream = pool.Writer.Value;
            this.CrashPoint("startup-before-tail-trim");
            stream.SetLength(length);
            stream.FlushToDisk();
            this.CrashPoint("startup-after-tail-trim");
            trailingLength = 0;
        }
    }
}
