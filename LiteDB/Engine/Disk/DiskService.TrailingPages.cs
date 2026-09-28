using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        /// <summary>The data file ends in a partial page that a writable open trims.</summary>
        internal bool HasTrailingDataPage => _dataTrailingLength != 0;

        /// <summary>
        /// Remove incomplete trailing pages only after the data header was validated.
        /// </summary>
        internal void TrimTrailingPages()
        {
            if (_readOnly) return;

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
            if (pool == _dataPool) this.SyncDataBarrier(stream);
            else this.SyncLogBarrier(stream);
            this.CrashPoint("startup-after-tail-trim");
            trailingLength = 0;
        }
    }
}
