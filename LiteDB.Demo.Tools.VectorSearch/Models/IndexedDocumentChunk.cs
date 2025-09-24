using System;
using LiteDB;

namespace LiteDB.Demo.Tools.VectorSearch.Models
{
    public sealed class IndexedDocumentChunk
    {
        public ObjectId Id { get; set; } = ObjectId.Empty;

        public string Path { get; set; } = string.Empty;

        public int ChunkIndex { get; set; }

        public string Snippet { get; set; } = string.Empty;

        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}

