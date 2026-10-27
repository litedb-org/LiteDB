using System;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        internal void ValidateHost() => _state.Validate();
        internal void StopHost(Exception error) => _state.Stop(error);

        internal void ValidateBackgroundCheckpointRelease()
        {
            if (_state.CheckpointLifetime != null) _walIndex.ValidateCheckpointJoin();
        }

        internal bool OwnsBackgroundCheckpoint => _state.CheckpointLifetime != null;

        internal void ThrowBackgroundCheckpointFailure(Exception cleanup = null) => _state.CheckpointLifetime?.ThrowIfFailed(cleanup);

#if DEBUG || TESTING
        // Slice 5 test installation. No production open binds these services yet.
        internal AutomaticCheckpointCoordinator InstallDirectCheckpointLifetime()
        {
            if (_host == null) throw new InvalidOperationException("Only a process-owned Direct core may own the coordinator.");
            return _host.Engine.BindDirectCheckpointLifetime();
        }
#endif

        private AutomaticCheckpointCoordinator BindDirectCheckpointLifetime()
        {
            _state.Validate();
            if (_state.CheckpointLifetime == null)
                _ = new DirectCheckpointLifetime(this, _state, _disk, _walIndex, _header.Pragmas.Checkpoint);
            return _state.CheckpointLifetime.Worker;
        }

        private void CopyHostServices()
        {
            var core = _host.Engine;
            _locker = core._locker;
            _disk = core._disk;
            _walIndex = core._walIndex;
            _header = core._header;
            _sortDisk = core._sortDisk;
            _state = core._state;
            _sequences = core._sequences;
            _monitor = core._monitor.CreateOwner();
            if (_lifetime != null) _lifetime.Owner = _monitor;
            _systemCollections = new Lazy<System.Collections.Generic.Dictionary<string, SystemCollection>>(this.CreateSystemCollections);
        }

    }
}
