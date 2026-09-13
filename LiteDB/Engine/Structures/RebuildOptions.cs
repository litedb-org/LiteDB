using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// </summary>
    public class RebuildOptions
    {
        /// <summary>
        /// A random BuildID identifier
        /// </summary>
        private string _buildId = Guid.NewGuid().ToString("d").ToLower().Substring(6);

        /// <summary>
        /// Rebuild database with a new password
        /// </summary>
        public string Password { get; set; } = null;

        /// <summary>
        /// Define a new collation when rebuild
        /// </summary>
        public Collation Collation { get; set; } = null;

        /// <summary>
        /// When set true, if any problem occurs in rebuild, a _rebuild_errors collection
        /// will contains all errors found
        /// </summary>
        public bool IncludeErrorReport { get; set; } = true;

        /// <summary>
        /// After run rebuild process, get a error report (empty if no error detected)
        /// </summary>
        internal IList<FileReaderError> Errors { get; } = new List<FileReaderError>();

        /// <summary>
        /// Get a list of errors during rebuild process
        /// </summary>
        public IEnumerable<BsonDocument> GetErrorReport()
        {
            var docs = this.Errors.Select(x => new BsonDocument
            {
                ["buildId"] = _buildId,
                ["created"] = x.Created,
                ["pageID"] = (int)x.PageID,
                ["positionID"] = (long)x.Position,
                ["origin"] = x.Origin.ToString(),
                ["pageType"] = x.PageType.ToString(),
                ["message"] = x.Message,
                ["exception"] = new BsonDocument
                {
                    ["code"] = (x.Exception is LiteException lex ? lex.ErrorCode : -1),
                    ["hresult"] = x.Exception.HResult,
                    ["type"] = x.Exception.GetType().FullName,
                    ["inner"] = x.Exception.InnerException?.Message,
                    ["stacktrace"] = x.Exception.StackTrace
                },
            });

            return docs;
        }
    }
}