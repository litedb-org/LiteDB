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
            // run build service
            var rebuilder = new RebuildService(_settings);
            var options = new RebuildOptions
            {
                Collation = collation,
                Password = _settings.Password,
                IncludeErrorReport = true
            };

            // run rebuild process
            rebuilder.Rebuild(options);
        }
    }
}