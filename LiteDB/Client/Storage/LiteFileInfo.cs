using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB
{
    /// <summary>
    /// Represents a file inside storage collection
    /// </summary>
    public class LiteFileInfo<TFileId>
    {
        public TFileId Id { get; internal set; }

        [BsonField("filename")]
        public string Filename { get; internal set; }

        [BsonField("mimeType")]
        public string MimeType { get; internal set; }

        [BsonField("length")]
        public long Length { get; internal set; } = 0;

        [BsonField("chunks")]
        public int Chunks { get; internal set; } = 0;

        [BsonField("uploadDate")]
        public DateTime UploadDate { get; internal set; } = DateTime.Now;

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
        /// Open file stream to read from database
        /// </summary>
        public Task<LiteFileStream<TFileId>> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LiteFileStream<TFileId>(_files, _chunks, this, _fileId, FileAccess.Read));
        }

        /// <summary>
        /// Open file stream to write to database
        /// </summary>
        public async Task<LiteFileStream<TFileId>> OpenWriteAsync(CancellationToken cancellationToken = default)
        {
            if (this.Length > 0)
            {
                var deleted = await _chunks.DeleteManyAsync("_id BETWEEN { f: @0, n: 0 } AND { f: @0, n: @1 }", cancellationToken, _fileId, int.MaxValue).ConfigureAwait(false);

                ENSURE(deleted == this.Chunks);

                this.Length = 0;
                this.Chunks = 0;
            }

            return new LiteFileStream<TFileId>(_files, _chunks, this, _fileId, FileAccess.Write);
        }

        /// <summary>
        /// Copy file content to another stream
        /// </summary>
        public async Task CopyToAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            await using var reader = await this.OpenReadAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await reader.CopyToAsync(stream).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// Save file content to a external file
        /// </summary>
        public async Task SaveAsAsync(string filename, bool overwritten = true, CancellationToken cancellationToken = default)
        {
            if (filename.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(filename));

            using var file = new FileStream(filename, overwritten ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var stream = await this.OpenReadAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await stream.CopyToAsync(file).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}