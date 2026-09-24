using System;
using LiteDB;

namespace LiteDB.Demo.Tools.VectorSearch.Models
{
    public sealed class IndexedDocument
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;

        public string Path { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Preview { get; set; } = string.Empty;

        public float[] Embedding { get; set; } = Array.Empty<float>();

        public DateTime LastModifiedUtc { get; set; }

        public long SizeBytes { get; set; }

        public string ContentHash { get; set; } = string.Empty;

        public DateTime IngestedUtc { get; set; }
    }
}

