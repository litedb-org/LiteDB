#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using LiteDB.Engine;
using LiteDB.Utils;

namespace LiteDB.Client.Shared
{
    // Main and WRITE already belong to the caller's core. Bootstrap closes the
    // attach race; converting this participant's DMS lock excludes every peer,
    // including processes which have not yet published a slot.
    internal sealed class SharedMemoryMaintenance : IDisposable
    {
        private readonly SharedMemoryReaderTable _table;
        private readonly EngineSettings _settings;
        private IDisposable _path;
        private Mutex _bootstrap;
        private bool _held, _exclusive, _converted, _structural, _disposed;
        internal SharedMemoryMaintenance(SharedMemoryReaderTable table, EngineSettings settings)
        {
            _table = table;
            _settings = settings;
            table.BeginMaintenance();
            try
            {
                table.ValidateWrite();
                _path = NativeAdmission.EnterPath(settings.Filename);
                _bootstrap = SharedMutexFactory.Create(SharedMutexNameFactory.CreateUsingSha1(
                    SharedMemoryAuthority.PathFor(settings.Filename)) + ".Bootstrap");
                try { _held = _bootstrap.WaitOne(0); } catch (AbandonedMutexException) { _held = true; }
                if (!_held)
                    throw new InvalidOperationException("Shared maintenance requires the only participant. Close all other processes, facades and readers first.");
                if (!table.Authority.Locks.ExclusiveDms)
                {
                    if (!table.Authority.Locks.TryExclusiveDms())
                        throw new InvalidOperationException("Shared maintenance requires the only participant. Close all other processes, facades and readers first.");
                    _converted = true;
                }
                _exclusive = true;
                Stage("exclusive");
                table.StructuralBegin();
                _structural = true;
                settings.SharedFileHandles?.CloseIdle();
                Stage("fenced");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_exclusive)
                {
                    // Installation's native admission has already followed the live
                    // inode, including bounded rollback which left a replacement live.
                    var authority = _table.Authority;
                    var identity = _table.Admission.Identity;
                    if (!authority.Header.MatchesIdentity(identity))
                    {
                        Stage("before-rekey");
                        authority.Rekey(_settings, identity);
                        Stage("rekeyed");
                    }
                    _table.Settings.Password = _settings.Password;
                    _table.Settings.Collation = _settings.Collation;
                    _table.Settings.CompactStorage = _settings.CompactStorage;
                    if (_structural) _table.StructuralEnd(-1);
                    Stage("published");
                }
            }
            catch
            {
                _table.FenceUncertain();
                throw;
            }
            finally
            {
                try
                {
                    // A nested replacement lease must leave its outer DMS conversion
                    // intact. Only the outermost scope returns to shared admission.
                    _table.EndMaintenance();
                    if (_converted) _table.Authority.Locks.DowngradeDms();
                }
                finally
                {
                    try { if (_held) _bootstrap.ReleaseMutex(); }
                    finally { try { _bootstrap?.Dispose(); } finally { _path?.Dispose(); } }
                }
            }
        }

#if DEBUG || TESTING
        internal static Action<string> MaintenanceStage;
#endif
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("TESTING")]
        internal static void Stage(string stage)
        {
#if DEBUG || TESTING
            Reachability.FaultPoint("MaintenanceStage");
            MaintenanceStage?.Invoke(stage);
#endif
        }
    }
}
#endif
