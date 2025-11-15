using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents metadata and access methods for a file stored in a <see cref="ILiteStorage{TFileId}"/> collection.
    /// </summary>
    /// <typeparam name="TFileId">The type used for file identifiers.</typeparam>
    /// <remarks>
    /// <para>
    /// <see cref="LiteFileInfo{TFileId}"/> contains all metadata about a stored file including its ID, filename, size, upload date,
    /// MIME type, and custom metadata. It also provides methods to access the file content through streams or save it to disk.
    /// </para>
    /// <para>
    /// Instances of this class are typically obtained through <see cref="ILiteStorage{TFileId}"/> query methods such as
    /// <see cref="ILiteStorage{TFileId}.FindById"/> or <see cref="ILiteStorage{TFileId}.Find(BsonExpression)"/>.
    /// </para>
    /// </remarks>
    public class LiteFileInfo<TFileId>
    {
        /// <summary>
        /// Gets the unique identifier of the file.
        /// </summary>
        /// <remarks>
        /// This is the primary key used to identify and retrieve the file from storage.
        /// </remarks>
        public TFileId Id { get; internal set; }

        /// <summary>
        /// Gets the filename associated with this file (for display and reference purposes).
        /// </summary>
        /// <remarks>
        /// The filename does not need to be unique and is primarily used for display and metadata purposes.
        /// The actual unique identifier is the <see cref="Id"/> property.
        /// </remarks>
        [BsonField("filename")]
        public string Filename { get; internal set; }

        /// <summary>
        /// Gets the MIME type (content type) of the file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The MIME type can be set during upload to indicate the file's content type (e.g., "text/plain", "application/pdf", "image/jpeg").
        /// </para>
        /// <para>
        /// This property may be <see langword="null"/> or empty if no MIME type was specified during upload.
        /// </para>
        /// </remarks>
        [BsonField("mimeType")]
        public string MimeType { get; internal set; }

        /// <summary>
        /// Gets the total size of the file in bytes.
        /// </summary>
        /// <remarks>
        /// This represents the actual content size of the file, not including storage overhead from chunking or metadata.
        /// </remarks>
        [BsonField("length")]
        public long Length { get; internal set; } = 0;

        /// <summary>
        /// Gets the number of chunks the file is divided into for storage.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Files are stored in chunks to efficiently handle large files. Each chunk is a separate document in the chunks collection.
        /// </para>
        /// <para>
        /// The chunk size is determined by the storage implementation (typically 1 MB per chunk).
        /// </para>
        /// </remarks>
        [BsonField("chunks")]
        public int Chunks { get; internal set; } = 0;

        /// <summary>
        /// Gets the date and time when the file was uploaded to the storage.
        /// </summary>
        /// <remarks>
        /// This timestamp is automatically set when the file is first uploaded and is not updated if the file is replaced.
        /// </remarks>
        [BsonField("uploadDate")]
        public DateTime UploadDate { get; internal set; } = DateTime.Now;

        /// <summary>
        /// Gets or sets the custom metadata document associated with this file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The metadata document can contain any application-specific information such as content type, author, tags,
        /// version information, or any other custom data.
        /// </para>
        /// <para>
        /// This property is never <see langword="null"/>; it defaults to an empty <see cref="BsonDocument"/>.
        /// Metadata can be queried and updated using <see cref="ILiteStorage{TFileId}.SetMetadata"/>.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var fileInfo = db.FileStorage.FindById("doc123");
        /// fileInfo.Metadata["author"] = "John Doe";
        /// fileInfo.Metadata["version"] = 2;
        /// db.FileStorage.SetMetadata("doc123", fileInfo.Metadata);
        /// </code>
        /// </example>
        [BsonField("metadata")]
        public BsonDocument Metadata { get; set; } = new BsonDocument();

        // database instances references
        private BsonValue _fileId;
        private ILiteCollection<LiteFileInfo<TFileId>> _files;
        private ILiteCollection<BsonDocument> _chunks;

        internal void SetReference(BsonValue fileId, ILiteCollection<LiteFileInfo<TFileId>> files, ILiteCollection<BsonDocument> chunks)
        {
            _fileId = fileId;
            _files = files;
            _chunks = chunks;
        }

        /// <summary>
        /// Opens a read-only stream to access the file content from the database.
        /// </summary>
        /// <returns>
        /// A <see cref="LiteFileStream{TFileId}"/> that provides read-only access to the file content.
        /// The stream must be disposed after use.
        /// </returns>
        /// <remarks>
        /// <para>
        /// The returned stream supports reading and seeking operations. Data is loaded from the database
        /// lazily as you read from the stream, allowing efficient processing of large files.
        /// </para>
        /// <para>
        /// Always dispose the stream to release resources.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var fileInfo = db.FileStorage.FindById("myfile.txt");
        /// using (var stream = fileInfo.OpenRead())
        /// using (var reader = new StreamReader(stream))
        /// {
        ///     string content = reader.ReadToEnd();
        ///     Console.WriteLine(content);
        /// }
        /// </code>
        /// </example>
        public LiteFileStream<TFileId> OpenRead()
        {
            return new LiteFileStream<TFileId>(_files, _chunks, this, _fileId, FileAccess.Read);
        }

        /// <summary>
        /// Opens a write-only stream to replace the file content in the database.
        /// </summary>
        /// <returns>
        /// A <see cref="LiteFileStream{TFileId}"/> that provides write-only access to overwrite the file content.
        /// The stream must be disposed to ensure all data is flushed and saved.
        /// </returns>
        /// <remarks>
        /// <para>
        /// Opening a file for writing replaces its existing content. All existing chunks are deleted and replaced
        /// with new data as you write to the stream.
        /// </para>
        /// <para>
        /// The file's metadata (including <see cref="Length"/> and <see cref="Chunks"/>) is automatically updated
        /// when the stream is disposed.
        /// </para>
        /// <para>
        /// Always dispose the stream (preferably using a <c>using</c> statement) to ensure data is properly saved.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var fileInfo = db.FileStorage.FindById("myfile.txt");
        /// using (var stream = fileInfo.OpenWrite())
        /// using (var writer = new StreamWriter(stream))
        /// {
        ///     writer.WriteLine("Updated content");
        /// }
        /// // File is now updated with new content
        /// </code>
        /// </example>
        public LiteFileStream<TFileId> OpenWrite()
        {
            return new LiteFileStream<TFileId>(_files, _chunks, this, _fileId, FileAccess.Write);
        }

        /// <summary>
        /// Copies the entire file content to the specified stream.
        /// </summary>
        /// <param name="stream">The destination stream where the file content will be written. The stream must be writable.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// This method reads the entire file content from the database and writes it to the <paramref name="stream"/>.
        /// The destination stream is not disposed and remains open after the copy operation completes.
        /// </para>
        /// <para>
        /// Data is written starting at the current position of the destination stream.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var fileInfo = db.FileStorage.FindById("document.pdf");
        /// using (var memoryStream = new MemoryStream())
        /// {
        ///     fileInfo.CopyTo(memoryStream);
        ///     byte[] fileData = memoryStream.ToArray();
        ///     Console.WriteLine($"Copied {fileData.Length} bytes to memory");
        /// }
        /// </code>
        /// </example>
        public void CopyTo(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using (var reader = this.OpenRead())
            {
                reader.CopyTo(stream);
            }
        }

        /// <summary>
        /// Saves the file content to a file in the file system.
        /// </summary>
        /// <param name="filename">The full path where the file should be saved in the file system.</param>
        /// <param name="overwritten">
        /// <para>If <see langword="true"/>, overwrites the file if it already exists.</para>
        /// <para>If <see langword="false"/>, throws an exception if the file exists.</para>
        /// </param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="filename"/> is <see langword="null"/> or whitespace.</exception>
        /// <exception cref="IOException">Thrown when the file already exists and <paramref name="overwritten"/> is <see langword="false"/>, 
        /// or when an I/O error occurs while writing the file.
        /// </exception>
        /// <exception cref="DirectoryNotFoundException">Thrown when the directory path does not exist.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown when the caller does not have the required permission.</exception>
        /// <remarks>
        /// <para>
        /// This method reads the entire file content from the database and writes it to a file at the specified path.
        /// The directory must exist; it will not be created automatically.
        /// </para>
        /// <para>
        /// The original file in the database is not modified or deleted.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var fileInfo = db.FileStorage.FindById("backup001");
        /// 
        /// // Save and overwrite if exists (default)
        /// fileInfo.SaveAs(@"C:\backups\database.bak");
        /// 
        /// // Save only if file doesn't exist
        /// try
        /// {
        ///     fileInfo.SaveAs(@"C:\backups\database.bak", overwritten: false);
        /// }
        /// catch (IOException)
        /// {
        ///     Console.WriteLine("File already exists");
        /// }
        /// </code>
        /// </example>
        public void SaveAs(string filename, bool overwritten = true)
        {
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));

            using (var file = File.Open(filename, overwritten ? FileMode.Create : FileMode.CreateNew))
            {
                using (var stream = this.OpenRead())
                {
                    stream.CopyTo(file);
                }
            }
        }
    }
}