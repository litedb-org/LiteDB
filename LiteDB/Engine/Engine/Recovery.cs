using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Recovery datafile using a rebuild process. Run only on "Open" database
        /// </summary>
        private void Recovery(Collation collation)
        {
            // A healthy read-only file needs only shared ownership, even when
            // automatic recovery or legacy upgrade was requested. Obtain write
            // ownership only once an actual replacement is necessary.
            if (_settings.ReadOnly)
            {
                this.ReleaseOwnership();
                _fileOwnership = FileOwnership.Acquire(_settings, forceExclusive: true);
            }
            // run build service
            var rebuilder = new RebuildService(_settings);
            var options = new RebuildOptions
            {
                Collation = collation,
                Password = _settings.Password,
                IncludeErrorReport = true
            };

            // run rebuild process
            this.RebuildWithOwnership(rebuilder, options, null);
            if (_settings.ReadOnly)
            {
                // No reader services are open yet. Reacquire shared ownership
                // before Open reads the replacement header/WAL, so even a writer
                // admitted between these leases cannot leave a stale snapshot.
                this.ReleaseOwnership();
                _fileOwnership = FileOwnership.Acquire(_settings);
            }
        }
        private long RebuildWithOwnership(RebuildService rebuilder, RebuildOptions options, Collation collation)
        {
            var original = _fileOwnership;
            try
            {
                return rebuilder.Rebuild(options, collation, replacement => _fileOwnership = replacement);
            }
            finally
            {
                if (!ReferenceEquals(original, _fileOwnership)) original?.Dispose();
            }
        }
    }
}
