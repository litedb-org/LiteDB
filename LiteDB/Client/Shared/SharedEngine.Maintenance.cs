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
                using var maintenance = _settings.SharedMaintenance?.Invoke();
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
                    try
                    {
                        var result = _engine.Rebuild(options);
#if NET8_0_OR_GREATER
                        if (_readers is LiteDB.Client.Shared.SharedMemoryReaders mapped)
                            mapped.Table.Replaced(_engine.ReadVersion);
#endif
                        return result;
                    }
                    catch
                    {
                        // Rebuild can have closed the core before failing. Drain it
                        // before this facade can reuse a disposed or replaced state.
                        if (_settings.SharedOneFile) _pin?.RequestRelease(force: true);
                        throw;
                    }
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
