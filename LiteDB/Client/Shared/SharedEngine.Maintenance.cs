using System;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB
{
    public partial class SharedEngine
    {
        public long Rebuild(RebuildOptions options)
        {
            return WriteDatabase(() =>
            {
                if (_settings.SharedOneFile) throw new NotSupportedException("ABI-2 maintenance requires slice 9 qualification.");
                // Publish before inspecting leases: a cached reader may be admitting
                // without the database mutex and must not accept a replaced file set.
                _settings.CoordinationSignals?.StructuralBegin();
                try
                {
                    if (_readers.OldestVersion().HasValue)
                    {
                        Reachability.Sometimes("refusal:shared-rebuild-with-open-readers");
                        throw new LiteException(0, "Close shared readers before rebuilding the database.");
                    }
                    _handles?.CloseIdle();
                    return _engine.Rebuild(options);
                }
                finally
                {
                    _handles?.CloseIdle();
                    _settings.CoordinationSignals?.StructuralEnd(-1);
                }
            });
        }

    }
}
