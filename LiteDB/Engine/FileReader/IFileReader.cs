using System;
using System.Collections.Generic;

namespace LiteDB.Engine
{
    /// <summary>
    /// Interface to read current or old datafile structure - Used to shirnk/upgrade datafile from old LiteDB versions
    /// </summary>
    interface IFileReader : IDisposable
    {
        /// <summary>
        /// Open and initialize file reader (run before any other command)
        /// </summary>
        void Open();

        /// <summary>
        /// Get all database pragma variables
        /// </summary>
        IDictionary<string, BsonValue> GetPragmas();

        /// <summary>
        /// Get all collections name from database
        /// </summary>
        IEnumerable<string> GetCollections();

        /// <summary>
        /// Get all indexes from collection (except _id index)
        /// </summary>
        IEnumerable<IndexInfo> GetIndexes(string name);

        /// <summary>
        /// Get all documents from a collection
        /// </summary>
        IEnumerable<BsonDocument> GetDocuments(string collection);

        /// <summary>
        /// Readable fields of the collection's damaged documents (after GetDocuments was enumerated)
        /// </summary>
        IEnumerable<BsonDocument> GetSalvagedDocuments(string collection);

        /// <summary>
        /// Report a salvaged document that could not be kept
        /// </summary>
        void RejectSalvagedDocument(string collection, BsonDocument document, string reason);
    }
}