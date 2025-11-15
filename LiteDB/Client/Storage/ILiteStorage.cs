using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;

namespace LiteDB
{
    /// <summary>
    /// Provides file storage capabilities within a LiteDB database, allowing files to be stored and retrieved alongside regular documents.
    /// </summary>
    /// <typeparam name="TFileId">The type used for file identifiers. Common types include <see cref="string"/>, <see cref="int"/>, <see cref="long"/>, and <see cref="Guid"/>.</typeparam>
    /// <remarks>
    /// <para>
    /// <see cref="ILiteStorage{TFileId}"/> implements a GridFS-like file storage system within LiteDB, storing files in two collections:
    /// a metadata collection (default: <c>_files</c>) and a chunks collection (default: <c>_chunks</c>).
    /// </para>
    /// <para>
    /// Files are stored in chunks to efficiently handle large files without consuming excessive memory. Each file's metadata
    /// includes information such as filename, upload date, file size, and custom metadata.
    /// </para>
    /// <para>
    /// The default file storage instance is available via <see cref="LiteDatabase.FileStorage"/> and uses <see cref="string"/> as the file ID type.
    /// Custom storage instances with different ID types or collection names can be created using <see cref="LiteDatabase.GetStorage{TFileId}"/>.
    /// </para>
    /// </remarks>
    public interface ILiteStorage<TFileId>
    {
        /// <summary>
        /// Finds a file by its ID and returns the file's metadata information.
        /// </summary>
        /// <param name="id">The unique identifier of the file to find.</param>
        /// <returns>
        /// A <see cref="LiteFileInfo{TFileId}"/> instance containing the file's metadata, or <see langword="null"/> if no file with the specified ID exists.
        /// </returns>
        /// <remarks>
        /// This method only retrieves the file's metadata (filename, upload date, size, etc.) without loading the actual file content.
        /// Use <see cref="OpenRead"/> or <see cref="Download(TFileId, Stream)"/> to access the file data.
        /// </remarks>
        LiteFileInfo<TFileId> FindById(TFileId id);

        /// <summary>
        /// Finds all files that match the specified predicate expression.
        /// </summary>
        /// <param name="predicate">A <see cref="BsonExpression"/> used to filter files based on their metadata.</param>
        /// <returns>An enumerable collection of <see cref="LiteFileInfo{TFileId}"/> instances that match the predicate.</returns>
        /// <remarks>
        /// The predicate can query any field in the file metadata, including custom metadata fields stored in the <c>metadata</c> property.
        /// </remarks>
        IEnumerable<LiteFileInfo<TFileId>> Find(BsonExpression predicate);

        /// <summary>
        /// Finds all files that match the specified predicate string with optional parameters.
        /// </summary>
        /// <param name="predicate">A string expression used to filter files (e.g., <c>"uploadDate >= @date"</c>).</param>
        /// <param name="parameters">A <see cref="BsonDocument"/> containing parameter values referenced in the predicate.</param>
        /// <returns>An enumerable collection of <see cref="LiteFileInfo{TFileId}"/> instances that match the predicate.</returns>
        /// <remarks>
        /// Parameters in the predicate are referenced using <c>@paramName</c> syntax and must be provided in the <paramref name="parameters"/> document.
        /// </remarks>
        IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, BsonDocument parameters);

        /// <summary>
        /// Finds all files that match the specified predicate string with positional parameters.
        /// </summary>
        /// <param name="predicate">A string expression used to filter files (e.g., <c>"uploadDate >= @0"</c>).</param>
        /// <param name="args">Positional parameter values referenced as <c>@0</c>, <c>@1</c>, <c>@2</c>, etc. in the predicate.</param>
        /// <returns>An enumerable collection of <see cref="LiteFileInfo{TFileId}"/> instances that match the predicate.</returns>
        IEnumerable<LiteFileInfo<TFileId>> Find(string predicate, params BsonValue[] args);

        /// <summary>
        /// Finds all files that match the specified LINQ predicate expression.
        /// </summary>
        /// <param name="predicate">A LINQ expression that filters files based on their <see cref="LiteFileInfo{TFileId}"/> metadata.</param>
        /// <returns>An enumerable collection of <see cref="LiteFileInfo{TFileId}"/> instances that match the predicate.</returns>
        /// <remarks>
        /// This method provides strongly-typed LINQ query support for file metadata. The expression is translated to a <see cref="BsonExpression"/> internally.
        /// </remarks>
        IEnumerable<LiteFileInfo<TFileId>> Find(Expression<Func<LiteFileInfo<TFileId>, bool>> predicate);

        /// <summary>
        /// Retrieves all files stored in the file storage collections.
        /// </summary>
        /// <returns>An enumerable collection of all <see cref="LiteFileInfo{TFileId}"/> instances in the storage.</returns>
        /// <remarks>
        /// Use this method with caution on large file collections as it will enumerate all file metadata.
        /// Consider using <see cref="Find(BsonExpression)"/> or other Find overloads to filter results.
        /// </remarks>
        IEnumerable<LiteFileInfo<TFileId>> FindAll();

        /// <summary>
        /// Determines whether a file with the specified ID exists in the storage.
        /// </summary>
        /// <param name="id">The unique identifier of the file to check.</param>
        /// <returns><see langword="true"/> if a file with the specified ID exists; otherwise, <see langword="false"/>.</returns>
        /// <remarks>
        /// This is a lightweight operation that only checks for the existence of the file metadata without loading the file content.
        /// </remarks>
        bool Exists(TFileId id);

        /// <summary>
        /// Opens or creates a file for writing and returns a stream for write operations.
        /// </summary>
        /// <param name="id">The unique identifier for the file. If a file with this ID already exists, it will be overwritten.</param>
        /// <param name="filename">The filename to associate with the file (for display/metadata purposes).</param>
        /// <param name="metadata">Optional custom metadata to store with the file. Default is <see langword="null"/>.</param>
        /// <returns>
        /// A <see cref="LiteFileStream{TFileId}"/> that can be used to write data to the file. 
        /// The stream must be disposed to ensure all data is flushed and metadata is saved.
        /// </returns>
        /// <remarks>
        /// <para>
        /// If a file with the specified <paramref name="id"/> already exists, it will be deleted and replaced with the new file.
        /// </para>
        /// <para>
        /// The <paramref name="metadata"/> parameter allows storing custom information with the file, such as content type,
        /// author, tags, or any other application-specific data.
        /// </para>
        /// <para>
        /// Always dispose the returned stream to ensure proper cleanup and data persistence.
        /// </para>
        /// </remarks>
        LiteFileStream<TFileId> OpenWrite(TFileId id, string filename, BsonDocument metadata = null);

        /// <summary>
        /// Uploads a file from a stream to the file storage.
        /// </summary>
        /// <param name="id">The unique identifier for the file. If a file with this ID already exists, it will be overwritten.</param>
        /// <param name="filename">The filename to associate with the file.</param>
        /// <param name="stream">The source stream containing the file data to upload. The stream will be read from its current position to the end.</param>
        /// <param name="metadata">Optional custom metadata to store with the file. Default is <see langword="null"/>.</param>
        /// <returns>A <see cref="LiteFileInfo{TFileId}"/> instance containing the metadata of the uploaded file.</returns>
        /// <remarks>
        /// <para>
        /// This method reads all data from the <paramref name="stream"/> starting at its current position and stores it in the file storage.
        /// The source stream is not disposed and its position will be at the end after the upload completes.
        /// </para>
        /// <para>
        /// If a file with the specified <paramref name="id"/> already exists, it will be deleted and replaced with the new file.
        /// </para>
        /// </remarks>
        LiteFileInfo<TFileId> Upload(TFileId id, string filename, Stream stream, BsonDocument metadata = null);

        /// <summary>
        /// Uploads a file from the file system to the file storage.
        /// </summary>
        /// <param name="id">The unique identifier for the file. If a file with this ID already exists, it will be overwritten.</param>
        /// <param name="filename">The full path to the file in the file system to upload.</param>
        /// <returns>A <see cref="LiteFileInfo{TFileId}"/> instance containing the metadata of the uploaded file.</returns>
        /// <remarks>
        /// <para>
        /// This method reads the file from the specified <paramref name="filename"/> path and stores it in the database.
        /// The original file in the file system is not modified or deleted.
        /// </para>
        /// <para>
        /// The uploaded file will use the filename (without path) from <paramref name="filename"/> as its stored filename.
        /// </para>
        /// </remarks>
        /// <exception cref="FileNotFoundException">Thrown when the specified file does not exist.</exception>
        /// <exception cref="IOException">Thrown when an I/O error occurs while reading the file.</exception>
        LiteFileInfo<TFileId> Upload(TFileId id, string filename);

        /// <summary>
        /// Updates the metadata of an existing file without modifying the file content.
        /// </summary>
        /// <param name="id">The unique identifier of the file whose metadata should be updated.</param>
        /// <param name="metadata">A <see cref="BsonDocument"/> containing the new metadata to store with the file.</param>
        /// <returns><see langword="true"/> if the file exists and its metadata was updated; <see langword="false"/> if the file does not exist.</returns>
        /// <remarks>
        /// <para>
        /// This method allows you to update custom metadata associated with a file without re-uploading the file content.
        /// The entire metadata document is replaced with the new <paramref name="metadata"/> document.
        /// </para>
        /// <para>
        /// The file must exist for the metadata to be updated. Use <see cref="Exists"/> to check if a file exists before calling this method.
        /// </para>
        /// </remarks>
        bool SetMetadata(TFileId id, BsonDocument metadata);

        /// <summary>
        /// Opens an existing file for reading and returns a stream for read operations.
        /// </summary>
        /// <param name="id">The unique identifier of the file to open.</param>
        /// <returns>
        /// A <see cref="LiteFileStream{TFileId}"/> that can be used to read the file content.
        /// The stream must be disposed after use.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The returned stream provides read-only access to the file content stored in the database.
        /// The file is loaded lazily as you read from the stream, so large files can be processed efficiently.
        /// </para>
        /// <para>
        /// Always dispose the returned stream to release resources.
        /// </para>
        /// </remarks>
        /// <exception cref="LiteException">Thrown when the file with the specified ID does not exist.</exception>
        LiteFileStream<TFileId> OpenRead(TFileId id);

        /// <summary>
        /// Downloads a file from the storage and writes its content to the specified stream.
        /// </summary>
        /// <param name="id">The unique identifier of the file to download.</param>
        /// <param name="stream">The destination stream where the file content will be written. The stream must be writable.</param>
        /// <returns>A <see cref="LiteFileInfo{TFileId}"/> instance containing the metadata of the downloaded file.</returns>
        /// <remarks>
        /// <para>
        /// This method reads the entire file content from the database and writes it to the <paramref name="stream"/>.
        /// The destination stream is not disposed and will remain open after the download completes.
        /// </para>
        /// <para>
        /// The file content is written starting at the current position of the <paramref name="stream"/>.
        /// </para>
        /// </remarks>
        /// <exception cref="LiteException">Thrown when the file with the specified ID does not exist.</exception>
        /// <exception cref="ArgumentException">Thrown when the stream is not writable.</exception>
        LiteFileInfo<TFileId> Download(TFileId id, Stream stream);

        /// <summary>
        /// Downloads a file from the storage and saves it to the file system.
        /// </summary>
        /// <param name="id">The unique identifier of the file to download.</param>
        /// <param name="filename">The full path where the file should be saved in the file system.</param>
        /// <param name="overwritten">If <see langword="true"/>, overwrites the file if it already exists; if <see langword="false"/>, throws an exception if the file exists.</param>
        /// <returns>A <see cref="LiteFileInfo{TFileId}"/> instance containing the metadata of the downloaded file.</returns>
        /// <remarks>
        /// <para>
        /// This method reads the entire file content from the database and writes it to a file at the specified <paramref name="filename"/> path.
        /// The directory path must exist; it will not be created automatically.
        /// </para>
        /// </remarks>
        /// <exception cref="LiteException">Thrown when the file with the specified ID does not exist in the database.</exception>
        /// <exception cref="IOException">Thrown when the file already exists and <paramref name="overwritten"/> is <see langword="false"/>, or when an I/O error occurs.</exception>
        /// <exception cref="DirectoryNotFoundException">Thrown when the directory path does not exist.</exception>
        LiteFileInfo<TFileId> Download(TFileId id, string filename, bool overwritten);

        /// <summary>
        /// Deletes a file from the storage, including all its chunks and metadata.
        /// </summary>
        /// <param name="id">The unique identifier of the file to delete.</param>
        /// <returns><see langword="true"/> if the file was found and deleted; <see langword="false"/> if the file does not exist.</returns>
        /// <remarks>
        /// <para>
        /// This method permanently removes the file and all associated data from the database.
        /// The operation cannot be undone unless you have a backup.
        /// </para>
        /// <para>
        /// If the file does not exist, the method returns <see langword="false"/> and no exception is thrown.
        /// </para>
        /// </remarks>
        bool Delete(TFileId id);
    }
}