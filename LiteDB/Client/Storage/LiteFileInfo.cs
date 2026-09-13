using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
        public LiteFileStream<TFileId> OpenRead()
        {
            return new LiteFileStream<TFileId>(_files, _chunks, this, _fileId, FileAccess.Read);
        }

        /// <summary>
        /// Open file stream to write to database
        /// </summary>
        public LiteFileStream<TFileId> OpenWrite()
        {
            return new LiteFileStream<TFileId>(_files, _chunks, this, _fileId, FileAccess.Write);
        }

        /// <summary>
        /// Copy file content to another stream
        /// </summary>
        public void CopyTo(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using (var reader = this.OpenRead())
            {
                reader.CopyTo(stream);
            }
        }

        /// <summary>
        /// Save file content to a external file
        /// </summary>
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