using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Implement a full rebuild database. Engine will be closed and re-created in another instance.
        /// A backup copy will be created with -backup extention. All data will be readed and re created in another database
        /// After run, will re-open database
        /// </summary>
        public long Rebuild(RebuildOptions options)
        {
            if (string.IsNullOrEmpty(_settings.Filename)) return 0; // works only with os file

            this.Close();

            // run build service
            var rebuilder = new RebuildService(_settings);

            // return how many bytes of diference from original/rebuild version
            var diff = rebuilder.Rebuild(options);

            // re-open engine
            this.Open();

            _state.Disposed = false;

            return diff;
        }

        /// <summary>
        /// Implement a full rebuild database. A backup copy will be created with -backup extention. All data will be readed and re created in another database
        /// </summary>
        public long Rebuild()
        {
            var collation = new Collation(this.Pragma(Pragmas.COLLATION));
            var password = _settings.Password;

            return this.Rebuild(new RebuildOptions { Password = password, Collation = collation });
        }

        internal void RebuildContent(IFileReader reader)
        {
            RebuildContent(reader, _disk.MAX_ITEMS_COUNT);
        }

        internal void RebuildContent(IFileReader reader, uint maxItemsCount)
        {
            var transaction = _monitor.GetTransaction(create: true, queryOnly: false, out _);

            try
            {
                foreach (var collection in reader.GetCollections())
                {
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, addIfNotExists: true);

                    var indexer = new IndexService(snapshot, _header.Pragmas.Collation, maxItemsCount);
                    var data = new DataService(snapshot, maxItemsCount);

                    foreach (var doc in reader.GetDocuments(collection))
                    {
                        transaction.Safepoint();
                        InsertDocument(snapshot, doc, BsonAutoId.ObjectId, indexer, data);
                    }

                    foreach (var idx in reader.GetIndexes(collection))
                    {
                        EnsureIndex(collection,
                                    idx.Name,
                                    BsonExpression.Create(idx.Expression),
                                    idx.Unique);
                    }
                }

                transaction.Commit();
                _monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                Close(ex);
                throw;
            }
        }
    }
}