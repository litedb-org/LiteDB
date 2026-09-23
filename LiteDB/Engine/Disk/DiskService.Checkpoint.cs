using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        internal void CheckpointStage(string stage)
        {
#if DEBUG || TESTING
            _state.CheckpointStage?.Invoke(stage);
#endif
        }

        internal void FlushLog()
        {
            if (this.GetFileLength(FileOrigin.Log) == 0) return;
            var stream = _writer.Value;
            lock (stream) SyncLogBarrier(stream);
        }

        internal IEnumerable<PageBuffer> ReadCheckpointPages(IEnumerable<PagePosition> pages)
        {
            var stream = _logPool.Rent();
            try
            {
                var bytes = new byte[PAGE_SIZE];
                foreach (var page in pages)
                {
                    stream.Position = page.Position;
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var count = stream.Read(bytes, offset, bytes.Length - offset);
                        if (count == 0) throw new EndOfStreamException("Incomplete checkpoint WAL frame.");
                        offset += count;
                    }
                    var buffer = new PageBuffer(bytes, 0, 0)
                    {
                        Position = BasePage.GetPagePosition(page.PageID),
                        ShareCounter = 0
                    };
                    buffer.Write(uint.MaxValue, BasePage.P_TRANSACTION_ID);
                    buffer.Write(false, BasePage.P_IS_CONFIRMED);
                    yield return buffer;
                }
            }
            finally { _logPool.Return(stream); }
        }
    }
}
