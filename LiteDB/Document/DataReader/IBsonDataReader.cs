using System;

namespace LiteDB
{
    /// <summary>
    /// Provides a forward-only reader for iterating through query results from SQL command execution.
    /// </summary>
    /// <remarks>
    /// <see cref="IBsonDataReader"/> is returned by <see cref="ILiteDatabase.Execute(string, BsonDocument)"/> and methods that execute SQL commands and provides
    /// efficient sequential access to query results without loading all documents into memory at once.
    /// </remarks>
    public interface IBsonDataReader : IDisposable
    {
        /// <summary>
        /// Gets the value of the specified field in the current document.
        /// </summary>
        /// <param name="field">The field name to retrieve.</param>
        /// <returns>The <see cref="BsonValue"/> of the specified field, or <see cref="BsonValue.Null"/> if the field does not exist.</returns>
        BsonValue this[string field] { get; }

        /// <summary>
        /// Gets the name of the collection from which the current document originated.
        /// </summary>
        string Collection { get; }

        /// <summary>
        /// Gets the current document as a <see cref="BsonValue"/>.
        /// </summary>
        /// <remarks>
        /// Returns <see cref="BsonValue.Null"/> if no document has been read yet or after all documents have been read.
        /// </remarks>
        BsonValue Current { get; }

        /// <summary>
        /// Gets a value indicating whether there are any results available to read.
        /// </summary>
        /// <returns><see langword="true"/> if the reader contains at least one document; otherwise, <see langword="false"/>.</returns>
        bool HasValues { get; }

        /// <summary>
        /// Advances the reader to the next document in the result set.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if there are more documents to read; <see langword="false"/> if the end of the result set has been reached.
        /// </returns>
        /// <remarks>
        /// Call this method before accessing the first document and after each document to move to the next one.
        /// The <see cref="Current"/> property is updated with the new document after each successful call.
        /// </remarks>
        bool Read();
    }
}