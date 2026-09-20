using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class WalIndexService
    {
        /// <summary>
        /// The caller owns the index write lock. A snapshot S resolves a page to its
        /// floor: the newest version at or below S. Keep the floor of every live
        /// snapshot and the newest version, which is the floor of every later one.
        /// No snapshot, in this or another process, can select any other frame.
        /// </summary>
        private List<long> FindObsoleteFrames(int[] liveVersions)
        {
            var obsolete = new HashSet<long>();
            var requiredVersions = new HashSet<int>();
            foreach (var versions in _index.Values)
            {
                if (versions.Count > 1) RemoveUnreachable(versions, liveVersions, obsolete);
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

        private static void RemoveUnreachable(List<KeyValuePair<int, long>> versions, int[] liveVersions, HashSet<long> obsolete)
        {
            var keep = new bool[versions.Count];
            keep[versions.Count - 1] = true;
            foreach (var live in liveVersions)
            {
                var floor = versions.FindLastIndex(frame => frame.Key <= live);
                if (floor >= 0) keep[floor] = true;
            }

            var reachable = new List<KeyValuePair<int, long>>();
            for (var i = 0; i < versions.Count; i++)
            {
                if (keep[i]) reachable.Add(versions[i]);
                else obsolete.Add(versions[i].Value);
            }
            versions.Clear();
            versions.AddRange(reachable);
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
