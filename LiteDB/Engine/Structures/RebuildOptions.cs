using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    /// <summary>
    /// Provides options for configuring the database rebuild process.
    /// </summary>
    /// <remarks>
    /// Rebuild operations recreate the database file to remove unused pages, change encryption, or modify collation settings.
    /// Use <see cref="ILiteDatabase.Rebuild"/> or <see cref="ILiteEngine.Rebuild"/> to perform a rebuild operation.
    /// </remarks>
    public class RebuildOptions
    {
        /// <summary>
        /// A random build identifier used to track errors from a specific rebuild operation.
        /// </summary>
        private string _buildId = Guid.NewGuid().ToString("d").ToLower().Substring(6);

        /// <summary>
        /// Gets or sets a new password to encrypt the rebuilt database.
        /// <para>Set to <see langword="null"/> to remove encryption.</para>
        /// </summary>
        public string Password { get; set; } = null;

        /// <summary>
        /// Gets or sets a new collation for the rebuilt database. Set to <see langword="null"/> to keep the current collation.
        /// </summary>
        /// <remarks>
        /// Changing the collation affects how strings are compared and sorted throughout the database.
        /// All indexes will be rebuilt using the new collation.
        /// </remarks>
        public Collation Collation { get; set; } = null;

        /// <summary>
        /// Gets or sets whether to include an error report if problems occur during the rebuild process. Default is <see langword="true"/>.
        /// </summary>
        /// <remarks>
        /// When <see langword="true"/>, any errors encountered during rebuild are collected and can be retrieved via <see cref="GetErrorReport"/>.
        /// The errors are also stored in a <c>_rebuild_errors</c> collection in the rebuilt database.
        /// </remarks>
        public bool IncludeErrorReport { get; set; } = true;

        /// <summary>
        /// Gets the list of errors encountered during the rebuild process.
        /// </summary>
        /// <remarks>
        /// This property is populated only after a rebuild operation completes. It will be empty if no errors were detected.
        /// </remarks>
        internal IList<FileReaderError> Errors { get; } = new List<FileReaderError>();

        /// <summary>
        /// Gets the error report as a collection of BSON documents containing detailed information about errors encountered during rebuild.
        /// </summary>
        /// <returns>
        /// An enumerable collection of <see cref="BsonDocument"/> instances, each representing an error.
        /// Returns an empty collection if no errors were detected or <see cref="IncludeErrorReport"/> is <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// Each error document contains:
        /// <list type="bullet">
        /// <item><description><c>buildId</c> - The unique identifier for this rebuild operation</description></item>
        /// <item><description><c>created</c> - When the error occurred</description></item>
        /// <item><description><c>pageID</c> - The page where the error was found</description></item>
        /// <item><description><c>positionID</c> - The position within the file</description></item>
        /// <item><description><c>origin</c> - The file origin (Data or Log)</description></item>
        /// <item><description><c>pageType</c> - The type of page that caused the error</description></item>
        /// <item><description><c>message</c> - A description of the error</description></item>
        /// <item><description><c>exception</c> - Detailed exception information including code, type, and stack trace</description></item>
        /// </list>
        /// </remarks>
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