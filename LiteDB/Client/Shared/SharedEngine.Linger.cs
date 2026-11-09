using LiteDB.Client.Shared;
using LiteDB.Engine;

namespace LiteDB
{
    public partial class SharedEngine
    {
        // Caller owns main. Protected snapshots never bypass ordinary admission.
        private LiteEngine TakeLingerSnapshot(bool abandoned)
        {
#if NET8_0_OR_GREATER
            if (this.CanScope && _settings.SharedOneFile && _settings.SharedLinger > 0)
                return (_readers as SharedMemoryReaders)?.Table.ProtectedSnapshot.Take(abandoned);
#endif
            return null;
        }

        private LiteEngine ParkLingerSnapshot(LiteEngine snapshot)
        {
#if NET8_0_OR_GREATER
            if (this.CanScope && _settings.SharedOneFile && _settings.SharedLinger > 0 && snapshot.CanRetainSharedSnapshot)
            {
                (_readers as SharedMemoryReaders).Table.ProtectedSnapshot.Park(snapshot);
                return null;
            }
#endif
            return snapshot;
        }
    }
}
