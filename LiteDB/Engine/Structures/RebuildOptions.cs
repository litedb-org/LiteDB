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
        /// Rebuild database with a new password. Null keeps the current password (or keeps the database
        /// unencrypted); an empty string is a valid password. Use <see cref="RemovePassword"/> to decrypt.
        /// </summary>
        public string Password { get; set; } = null;

        /// <summary>Choose compact writes or legacy BSON for the rebuilt file; null retains the engine setting. Vector data still requires v9.</summary>
        public bool? CompactStorage { get; set; }

        /// <summary>
        /// When set true, rebuild into an unencrypted database. Cannot be combined with <see cref="Password"/>.
        /// </summary>
        public bool RemovePassword { get; set; } = false;

        /// <summary>
        /// Password of the rebuilt database: losing encryption is never a side effect of an omitted Password.
        /// </summary>
        internal string ResolvePassword(string currentPassword)
        {
            if (this.RemovePassword == false) return this.Password ?? currentPassword;

            if (this.Password != null)
            {
                throw new ArgumentException("RebuildOptions cannot both set a Password and RemovePassword.");
            }

            return null;
        }

        /// <summary>
        /// Define a new collation when rebuilding. Null preserves the current database collation.
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
