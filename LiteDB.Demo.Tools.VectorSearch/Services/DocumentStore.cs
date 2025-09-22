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
        private const string CollectionName = "documents";

        private readonly LiteDatabase _database;
        private readonly ILiteCollection<IndexedDocument> _collection;
        private ushort? _vectorDimensions;

        public DocumentStore(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("Database path must be provided.", nameof(databasePath));
            }

            var fullPath = Path.GetFullPath(databasePath);
            _database = new LiteDatabase(fullPath);
            _collection = _database.GetCollection<IndexedDocument>(CollectionName);
            _collection.EnsureIndex(x => x.Path, true);
        }

        public IndexedDocument? FindByPath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return null;
            }

            return _collection.FindOne(x => x.Path == absolutePath);
        }

        public void EnsureVectorIndex(int dimensions)
        {
            if (dimensions <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Vector dimensions must be positive.");
            }

            var targetDimensions = (ushort)dimensions;
            if (_vectorDimensions == targetDimensions)
            {
                return;
            }

            _collection.EnsureIndex(x => x.Embedding, new VectorIndexOptions(targetDimensions, VectorDistanceMetric.Cosine));
            _vectorDimensions = targetDimensions;
        }

        public void Upsert(IndexedDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            _collection.Upsert(document);
        }

        public IEnumerable<IndexedDocument> TopNearest(float[] embedding, int count)
        {
            if (embedding == null)
            {
                throw new ArgumentNullException(nameof(embedding));
            }

            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "Quantity must be positive.");
            }

            return _collection.Query()
                .TopKNear(x => x.Embedding, embedding, count)
                .ToEnumerable();
        }

        public IReadOnlyCollection<string> GetTrackedPaths()
        {
            return _collection.FindAll()
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

            foreach (var doc in _collection.FindAll().Where(doc => !keep.Contains(doc.Path)))
            {
                _collection.Delete(doc.Id);
            }
        }

        public void Dispose()
        {
            _database.Dispose();
        }
    }
}
