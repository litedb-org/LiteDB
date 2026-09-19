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
            this.TrimTrailingPage(_logPool, _logLength + PAGE_SIZE, ref _logTrailingLength);
        }

        private void TrimTrailingPage(StreamPool pool, long length, ref long trailingLength)
        {
            if (trailingLength == 0) return;

            var stream = pool.Writer.Value;
            stream.SetLength(length);
            stream.FlushToDisk();
            trailingLength = 0;
        }
    }
}
