using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        /// <summary>
        /// The caller owns the index write lock. For each page keep its newest
        /// version at or below the watermark (the floor), and every newer version.
        /// A snapshot S >= watermark captured its index after that floor committed,
        /// so even another process's unchanged index cannot select an earlier frame.
        /// </summary>
        private List<long> FindObsoleteFrames(int watermark)
        {
            var obsolete = new HashSet<long>();
            var requiredVersions = new HashSet<int>();
            foreach (var versions in _index.Values)
            {
                var floor = versions.FindLastIndex(frame => frame.Key <= watermark);
                for (var i = 0; i < floor; i++) obsolete.Add(versions[i].Value);
                if (floor > 0) versions.RemoveRange(0, floor);
                foreach (var frame in versions) requiredVersions.Add(frame.Key);
            }

            // A confirmation may be a superseded page image while still confirming
            // other required pages. Keep it until the whole transaction is obsolete.
            foreach (var confirmation in _confirmationPositions.ToArray())
            {
                if (requiredVersions.Contains(confirmation.Key))
                {
                    obsolete.Remove(confirmation.Value);
                }
                else
                {
                    obsolete.Add(confirmation.Value);
                    _confirmationPositions.Remove(confirmation.Key);
                }
            }
            return obsolete.OrderBy(position => position).ToList();
        }

#if DEBUG || TESTING
        internal long[] SnapshotPositions(int version)
        {
            _indexLock.EnterReadLock();
            try
            {
                return _index.Values.Where(frames => frames.Any(frame => frame.Key <= version))
                    .Select(frames => frames.Last(frame => frame.Key <= version).Value).ToArray();
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }
#endif
    }
}
