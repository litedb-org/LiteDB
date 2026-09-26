using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LiteDB.Engine
{
    internal sealed class SchemaCatalog
    {
        private readonly Dictionary<uint, StorageSchema> _schemas = new Dictionary<uint, StorageSchema>();
        private readonly HashSet<ulong> _candidates = new HashSet<ulong>();
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

        internal StorageSchema Select(string[] fields, List<StorageSchema> pending, Func<ulong, bool> admit = null)
        {
            if (fields.Length == 0 || fields.Length > StorageSchema.MaxFields) return null;
            foreach (var schema in _schemas.Values) if (schema.Matches(fields)) return schema;
            foreach (var schema in pending) if (schema.Matches(fields)) return schema;
            if (Count + pending.Count >= StorageSchema.MaxSchemas) return null;
            // Admission is a hint only. Hash collisions can admit a shape earlier,
            // but persisted schemas and references always use the complete verified definition.
            var hash = 14695981039346656037UL;
            foreach (var field in fields)
            {
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
            if (fields.Any(f => StorageSchema.Utf8.GetByteCount(f) > StorageSchema.MaxNameBytes || f.IndexOf('\0') >= 0)) return null;
            var candidate = new StorageSchema((uint)(Count + pending.Count + 1), fields);
            if (candidate.Definition.Length + 12 > 4096) return null;
            pending.Add(candidate);
            return candidate;
        }

        internal void Add(StorageSchema schema) => _schemas.Add(schema.Id, schema);
    }
}
