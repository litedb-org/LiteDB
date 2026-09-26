using System;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Experimental coordinator: advance this read-only snapshot engine to the newest
        /// transactions appended since it opened or last advanced; see
        /// <see cref="WalIndexService.ExtendIndex"/> for the preconditions. The caller
        /// guarantees that no query runs on this engine meanwhile.
        /// </summary>
        internal int AdvanceSnapshot()
        {
            _state.Validate();
            if (!_settings.ReadOnly) throw new InvalidOperationException("Only a read-only snapshot engine can advance.");
            var version = _walIndex.ExtendIndex(_header);
            // Compact schemas are cached per collection page; a newer schema page may exist now.
            _disk.ClearSchemaCache();
            return version;
        }

#if DEBUG || TESTING
        /// <summary>Test hook: the WAL index and header bytes, for comparing an advanced snapshot with a fresh one.</summary>
        internal string DescribeSnapshot()
        {
            var header = new byte[PAGE_SIZE];
            Buffer.BlockCopy(_header.Buffer.Array, _header.Buffer.Offset, header, 0, PAGE_SIZE);
            return _walIndex.DescribeIndex() + ";h=" + Convert.ToBase64String(header);
        }
#endif
    }
}
