using System;
using System.Collections.Generic;
using System.Linq;

namespace LiteDB.Engine
{
    internal partial class DiskService
    {
        private readonly object _schemaCacheLock = new object();
        private readonly Dictionary<(uint Collection, int Version), SchemaCatalog> _schemaCache =
            new Dictionary<(uint Collection, int Version), SchemaCatalog>();
        internal long SchemaCacheBytes { get; private set; }
        private readonly Dictionary<(uint Collection, ulong Shape), (int Document, bool Repeated)> _schemaCandidates =
            new Dictionary<(uint Collection, ulong Shape), (int Document, bool Repeated)>();

        internal bool ObserveShape(uint collection, ulong shape, int document)
        {
            lock (_schemaCacheLock)
            {
                var key = (collection, shape);
                if (_schemaCandidates.TryGetValue(key, out var candidate))
                {
                    if (candidate.Repeated) return true;
                    if (candidate.Document == document) return false;
                    _schemaCandidates[key] = (document, true);
                    return true;
                }
                if (_schemaCandidates.Count >= 2048) _schemaCandidates.Clear();
                _schemaCandidates.Add(key, (document, false));
                return false;
            }
        }

        internal SchemaCatalog ReadSchemas(uint collection, int version, Func<SchemaCatalog> load)
        {
            var key = (collection, version);
            lock (_schemaCacheLock)
            {
                if (_schemaCache.TryGetValue(key, out var cached)) return cached;
            }
            // Only read snapshots publish here. A write snapshot owns its tentative catalog.
            var catalog = load();
            var bytes = catalog.Schemas.Sum(s => 128L + s.Definition.Length * 4L + s.Fields.Length * 64L);
            lock (_schemaCacheLock)
            {
                if (_schemaCache.TryGetValue(key, out var cached)) return cached;
                if (SchemaCacheBytes + bytes > 8 * 1024 * 1024 || _schemaCache.Count >= 32) ClearSchemaCache();
                if (bytes <= 8 * 1024 * 1024)
                {
                    _schemaCache.Add(key, catalog);
                    SchemaCacheBytes += bytes;
                }
            }
            return catalog;
        }

        internal void ClearSchemaCache()
        {
            lock (_schemaCacheLock)
            {
                _schemaCache.Clear();
                SchemaCacheBytes = 0;
            }
        }
    }
}
