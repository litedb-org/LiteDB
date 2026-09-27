using System;
using System.Collections.Generic;
using System.IO;

namespace LiteDB.Engine
{
    internal partial class Snapshot
    {
        private SchemaCatalog _schemaCatalog;
        internal bool CompactStorage => _disk.CompactStorage;
        internal bool ObserveShape(ulong hash) => _disk.ObserveShape(_collectionPage.PageID, hash, _compactDocumentHash);
        internal SchemaCatalog Schemas => _schemaCatalog ??= _mode == LockMode.Read
            ? _disk.ReadSchemas(_collectionPage.PageID, _readVersion, LoadSchemas)
            : LoadSchemas();

        private SchemaCatalog LoadSchemas()
        {
            try { return SchemaCatalog.Load(_collectionPage, id => GetPage<SchemaPage>(id)); }
            catch (Exception ex) when (ex is InvalidDataException || ex is LiteException || ex is IOException || ex is ArgumentException || ex is InvalidCastException)
            {
                throw new LiteException(LiteException.CORRUPT_DOCUMENT, ex,
                    "Corrupt schema catalog for collection '{0}', schema root {1}; document address unavailable: {2}",
                    _collectionName, _collectionPage.SchemaRoot, ex.Message);
            }
        }

        internal void CommitSchemas(List<StorageSchema> schemas)
        {
            RequireFileVersion(HeaderPage.COMPACT_FILE_VERSION);
            foreach (var schema in schemas)
            {
                var root = _collectionPage.SchemaRoot;
                var tail = _collectionPage.SchemaTail;
                var page = tail == uint.MaxValue ? null : GetPage<SchemaPage>(tail);
                if (page == null || !page.CanAppend(schema))
                {
                    var next = NewPage<SchemaPage>();
                    if (page != null) { page.NextPageID = next.PageID; page.IsDirty = true; }
                    page = next;
                    tail = page.PageID;
                    if (root == uint.MaxValue) root = tail;
                }
                page.Append(schema);
                _collectionPage.SetSchemaMetadata(root, tail, schema.Id);
                Schemas.Add(schema);
            }
        }

        private void DropSchemas(System.Action safepoint)
        {
            // Validate the complete chain before reclaiming it; never follow arbitrary corrupt links.
            var schemas = Schemas;
            var next = _collectionPage.SchemaRoot;
            while (next != uint.MaxValue)
            {
                var page = GetPage<SchemaPage>(next);
                next = page.NextPageID;
                page.MarkAsEmtpy();
                page.NextPageID = _transPages.FirstDeletedPageID;
                _transPages.FirstDeletedPageID = page.PageID;
                _transPages.DeletedPages++;
                safepoint();
            }
        }
    }
}
