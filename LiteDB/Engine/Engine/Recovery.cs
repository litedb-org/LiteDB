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
        /// Recover the data file using a rebuild process. Run only while opening a database.
        /// </summary>
        /// <param name="collation">Collation to use for the rebuilt database.</param>
        /// <param name="createBackup">Whether to retain the original data and log files.</param>
        /// <returns>The rebuild result whose cleanup must wait for validation.</returns>
        private RebuildResult Recovery(Collation collation, bool createBackup = true)
        {
            // run build service
            var rebuilder = new RebuildService(_settings);
            var options = new RebuildOptions
            {
                Collation = collation,
                Password = _settings.Password,
                IncludeErrorReport = true,
                CreateBackup = createBackup
            };

            // run rebuild process
            return rebuilder.Rebuild(options);
        }
    }
}
