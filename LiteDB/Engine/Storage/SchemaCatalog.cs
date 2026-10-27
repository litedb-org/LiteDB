using System;
using System.Collections.Generic;
using System.IO;

namespace LiteDB.Engine
{
    internal sealed class SchemaCatalog
    {
        private readonly Dictionary<uint, StorageSchema> _schemas = new Dictionary<uint, StorageSchema>();
        private readonly HashSet<ulong> _candidates = new HashSet<ulong>();
        private StorageSchema _preparedSchema;
        internal IEnumerable<StorageSchema> Schemas => _schemas.Values;
        internal int Count => _schemas.Count;

        internal static SchemaCatalog Load(CollectionPage collection, Func<uint, SchemaPage> read)
        {
            var result = new SchemaCatalog();
            var pageId = collection.SchemaRoot;
            var visited = new HashSet<uint>();
            uint tail = uint.MaxValue;
            while (pageId != uint.MaxValue)
            {
                if (visited.Count >= StorageSchema.MaxSchemas || !visited.Add(pageId)) throw new InvalidDataException("Schema page cycle/budget exceeded.");
                var page = read(pageId);
                if (page.ColID != collection.PageID) throw new InvalidDataException("Schema belongs to another collection.");
                foreach (var schema in page.ReadSchemas())
                {
                    if (schema.Id != result.Count + 1) throw new InvalidDataException("Duplicate or unordered schema id.");
                    result._schemas.Add(schema.Id, schema);
                }
                tail = pageId;
                pageId = page.NextPageID;
            }
            if (result.Count != collection.SchemaCount || tail != collection.SchemaTail) throw new InvalidDataException("Missing schema page/entry.");
            return result;
        }

        internal StorageSchema Get(uint id, ulong fingerprint)
        {
            if (!_schemas.TryGetValue(id, out var schema) || schema.Fingerprint != fingerprint)
                throw new InvalidDataException($"Missing schema or fingerprint mismatch: {id}.");
            return schema;
        }

        internal StorageSchema Select(BsonDocument document, List<StorageSchema> pending, Func<ulong, bool> admit = null)
        {
            if (document.Count == 0 || document.Count > StorageSchema.MaxFields) return null;
            foreach (var schema in _schemas.Values) if (schema.Matches(document)) return schema;
            foreach (var schema in pending) if (schema.Matches(document)) return schema;
            if (Count + pending.Count >= StorageSchema.MaxSchemas) return null;
            // Admission is a hint only. Hash collisions can admit a shape earlier,
            // but persisted schemas and references always use the complete verified definition.
            var hash = 14695981039346656037UL;
            foreach (var element in new CompactDocumentElements(document))
            {
                var field = element.Key;
                hash = unchecked((hash ^ (uint)field.Length) * 1099511628211UL);
                foreach (var character in field) hash = unchecked((hash ^ character) * 1099511628211UL);
            }
            var repeated = admit != null ? admit(hash) : _candidates.Contains(hash);
            if (!repeated)
            {
                if (_candidates.Count >= StorageSchema.MaxSchemas) _candidates.Clear();
                if (admit == null) _candidates.Add(hash);
                return null;
            }
            // A repeated shape whose payload did not save enough bytes would otherwise
            // rebuild its immutable definition on every admission attempt. Keep one plan
            // in this snapshot's catalog, and still perform admission on every attempt.
            var nextId = (uint)(Count + pending.Count + 1);
            if (_preparedSchema != null && _preparedSchema.Id == nextId &&
                _preparedSchema.Fields.Length == document.Count && _preparedSchema.Matches(document))
            {
                pending.Add(_preparedSchema);
                return _preparedSchema;
            }
            var fields = new string[document.Count];
            var index = 0;
            foreach (var element in new CompactDocumentElements(document))
            {
                var field = element.Key;
                if (StorageSchema.Utf8.GetByteCount(field) > StorageSchema.MaxNameBytes || field.IndexOf('\0') >= 0) return null;
                fields[index++] = field;
            }
            var candidate = new StorageSchema(nextId, fields);
            if (candidate.Definition.Length + 12 > 4096) return null;
            _preparedSchema = candidate;
            pending.Add(candidate);
            return candidate;
        }

        internal void Add(StorageSchema schema)
        {
            _schemas.Add(schema.Id, schema);
            _preparedSchema = null;
        }
    }
}
