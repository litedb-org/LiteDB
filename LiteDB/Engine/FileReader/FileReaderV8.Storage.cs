using System.Collections.Generic;

namespace LiteDB.Engine
{
    internal partial class FileReaderV8
    {
        private readonly Dictionary<uint, SchemaCatalog> _schemas = new Dictionary<uint, SchemaCatalog>();

        private SchemaCatalog ReadSchemas(uint collection)
        {
            if (_schemas.TryGetValue(collection, out var catalog)) return catalog;
            var page = new CollectionPage(ReadPage(collection, out _).GetValue().Buffer);
            catalog = SchemaCatalog.Load(page, id => new SchemaPage(ReadPage(id, out _).GetValue().Buffer));
            // Rebuild reads one collection at a time; retain at most one catalog.
            _schemas.Clear();
            _schemas.Add(collection, catalog);
            return catalog;
        }
    }
}
