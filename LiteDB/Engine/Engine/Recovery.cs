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
        private void Recovery(Collation collation, Exception openingError = null)
        {
            // run build service
            var rebuilder = new RebuildService(_settings);
            var options = new RebuildOptions
            {
                Collation = collation,
                Password = _settings.Password,
                IncludeErrorReport = true
            };

            if (openingError != null)
            {
                var damage = openingError as LegacyFileException;
                options.Errors.Add(new FileReaderError
                {
                    Stage = "opening",
                    Origin = FileOrigin.Data, PageType = damage?.PageType ?? PageType.Empty,
                    PageID = damage?.Address.PageID, Collection = damage?.Collection,
                    Message = openingError.Message, Exception = openingError
                });
            }

            // run rebuild process
            rebuilder.Rebuild(options);
        }
    }
}
