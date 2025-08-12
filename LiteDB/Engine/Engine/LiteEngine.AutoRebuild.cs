using System;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        private DateTime _lastAutoRebuildUtc = DateTime.MinValue;
        private static readonly TimeSpan _autoRebuildCooldown = TimeSpan.FromMinutes(5);

        private bool TryAutoRebuild(Exception ex, bool viaOpen = false)
        {
            try
            {
                if (!_settings.AutoRebuild) return false;
                if (!IsStructuralCorruption(ex)) return false;
                if (_autoRebuildInProgress) return false;
                if (DateTime.UtcNow - _lastAutoRebuildUtc < _autoRebuildCooldown) return false;

                _autoRebuildInProgress = true;
                _lastAutoRebuildUtc = DateTime.UtcNow;

                try
                {
                    if (viaOpen)
                        AutoRebuildAndReopenViaOpenPath();
                    else
                        AutoRebuildAndReopen();

                    return true;
                }
                finally
                {
                    _autoRebuildInProgress = false;
                }
            }
            catch
            {
                // nie wyciekaj wyjątków na zewnątrz – decyzja o rethrow zostaje w wyższym poziomie
                return false;
            }
        }
    }
}
