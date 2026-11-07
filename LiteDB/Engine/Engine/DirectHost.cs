using System;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        internal void ValidateHost() => _state.Validate();
        internal void StopHost(Exception error) => _state.Stop(error);

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
