namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        private PageBuffer ReadCommittedPage(uint pageID, bool latest, out FileOrigin origin,
            out long position, out int walVersion)
        {
            // A latest frame can be superseded after lookup and reclaimed by a
            // partial checkpoint: this snapshot's older pin does not protect it.
            // Pin before lookup and resolve at that version, never int.MaxValue.
            // A fresh per-page pin sees foreign commits without holding the index
            // lock across I/O or retaining every version for the whole census.
            // The free-page allocator uses this same latest-version path.
            var version = latest ? _walIndex.PinSnapshot() : _readVersion;
            try
            {
                position = _walIndex.GetPageIndex(pageID, version, out walVersion);
                origin = position == long.MaxValue ? FileOrigin.Data : FileOrigin.Log;
                if (origin == FileOrigin.Data) position = BasePage.GetPagePosition(pageID);
                return _reader.ReadPage(position, _mode == LockMode.Write, origin);
            }
            finally
            {
                // The returned buffer owns its bytes; failures must release the
                // temporary pin too, so later checkpoints can reclaim normally.
                if (latest) _walIndex.UnpinSnapshot(version);
            }
        }
    }
}
