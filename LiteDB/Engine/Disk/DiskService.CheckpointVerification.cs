using System.Collections.Generic;
using System.IO;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        internal bool TryVerifyCheckpointBody(ISet<uint> transactions, out uint body)
        {
            body = 0;
            var raw = ((ChecksummedWalStream)_writer.Value).RawStream;
            if (raw.GetType() != typeof(FileStream) || raw.Length % PAGE_SIZE != 0) return false;
            // BeginHeaderJournal materializes zero padding. Reuse a hash only if
            // that write will leave every byte hashed by this scan unchanged.
            var padding = new byte[raw.Length % WalChecksum.FrameSize];
            raw.Position = raw.Length - padding.Length;
            raw.ReadRequired(padding, 0, padding.Length);
            foreach (var value in padding) if (value != 0) return false;
            var header = new byte[PAGE_SIZE];
            var data = _dataPool.Writer.Value;
            data.Position = 0;
            data.ReadRequired(header, 0, header.Length);
            PageChecksum.Validate(new BufferSlice(header, 0, header.Length), 0);
            body = HeaderJournal.ComputeVerifiedBody(raw, raw.Length, header, _checksums, transactions);
            return true;
        }
    }
}
