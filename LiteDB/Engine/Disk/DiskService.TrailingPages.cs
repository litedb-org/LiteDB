using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
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
            this.CrashPoint("startup-before-tail-trim");
            // A partial trailing page holds nothing recovery reads: trimmed without ShrinkLog.
            if (pool == _dataPool)
            {
                this.UseDataWriter(data =>
                {
                    this.CountDataWrite();
                    data.SetLength(length);
                    this.SyncDataBarrier(data);
                });
            }
            else
            {
                var stream = pool.Writer.Value;
                stream.SetLength(length);
                this.SyncLogBarrier(stream);
            }
            this.CrashPoint("startup-after-tail-trim");
            trailingLength = 0;
        }
    }
}
