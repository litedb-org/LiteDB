using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Demo.Tools.VectorSearch.Models;

namespace LiteDB.Demo.Tools.VectorSearch.Services
{
    internal sealed class DocumentStore : IDisposable
    {
        private const string DocumentCollectionName = "documents";
        private const string ChunkCollectionName = "chunks";

        private readonly LiteDatabase _database;
        private readonly ILiteCollection<IndexedDocument> _documents;
        private readonly ILiteCollection<IndexedDocumentChunk> _chunks;
        private ushort? _chunkVectorDimensions;

        public DocumentStore(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("Database path must be provided.", nameof(databasePath));
            }

            var fullPath = Path.GetFullPath(databasePath);
            _database = new LiteDatabase(fullPath);
            _documents = _database.GetCollection<IndexedDocument>(DocumentCollectionName);
            _documents.EnsureIndex(x => x.Path, true);

            _chunks = _database.GetCollection<IndexedDocumentChunk>(ChunkCollectionName);
            _chunks.EnsureIndex(x => x.Path);
            _chunks.EnsureIndex(x => x.ChunkIndex);
        }

        public IndexedDocument? FindByPath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return null;
            }

            return _documents.FindOne(x => x.Path == absolutePath);
        }

        public void EnsureChunkVectorIndex(int dimensions)
        {
            if (dimensions <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Vector dimensions must be positive.");
            }

            var targetDimensions = (ushort)dimensions;
            if (_chunkVectorDimensions == targetDimensions)
            {
                return;
            }

            _chunks.EnsureIndex(x => x.Embedding, new VectorIndexOptions(targetDimensions, VectorDistanceMetric.Cosine));
            _chunkVectorDimensions = targetDimensions;
        }

        public void Upsert(IndexedDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            _documents.Upsert(document);
        }

        public void ReplaceDocumentChunks(string documentPath, IEnumerable<IndexedDocumentChunk> chunks)
        {
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                throw new ArgumentException("Document path must be provided.", nameof(documentPath));
            }

            _chunks.DeleteMany(chunk => chunk.Path == documentPath);

            if (chunks == null)
            {
                return;
            }

            foreach (var chunk in chunks)
            {
                if (chunk == null)
                {
                    continue;
                }

                chunk.Path = documentPath;

                if (chunk.Id == ObjectId.Empty)
                {
                    chunk.Id = ObjectId.NewObjectId();
                }

                _chunks.Insert(chunk);
            }
        }

        public IEnumerable<IndexedDocumentChunk> TopNearestChunks(float[] embedding, int count)
        {
            if (embedding == null)
            {
                throw new ArgumentNullException(nameof(embedding));
            }

            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Quantity must be positive.");
            }

            EnsureChunkVectorIndex(embedding.Length);

            return _chunks.Query()
                .TopKNear(x => x.Embedding, embedding, count)
                .ToEnumerable();
        }

        public IReadOnlyCollection<string> GetTrackedPaths()
        {
            return _documents.FindAll()
                .Select(doc => doc.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public void RemoveMissingDocuments(IEnumerable<string> existingDocumentPaths)
        {
            if (existingDocumentPaths == null)
            {
                return;
            }

            var keep = new HashSet<string>(existingDocumentPaths, StringComparer.OrdinalIgnoreCase);

            foreach (var doc in _documents.FindAll().Where(doc => !keep.Contains(doc.Path)))
            {
                _documents.Delete(doc.Id);
                _chunks.DeleteMany(chunk => chunk.Path == doc.Path);
            }
        }

        public void Dispose()
        {
            _database.Dispose();
        }
    }
}
