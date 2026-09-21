using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteDB.Vector;

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
            // Every omitted option keeps its current value; conflicting options fail before the engine closes.
            options = options ?? new RebuildOptions();
            var password = options.ResolvePassword(_settings.Password);

            if (string.IsNullOrEmpty(_settings.Filename)) return 0; // works only with os file

            var collation = options.Collation ?? new Collation(this.Pragma(Pragmas.COLLATION));

            // Rebuild replaces the database files and all engine services. Wait for
            // existing transactions to finish and prevent a new one from being
            // admitted between that wait and Close(). Close disposes the old lock,
            // so this exclusive lease intentionally is not released here.
            if (_locker.IsInTransaction) throw LiteException.AlreadyExistsTransaction();
            _locker.EnterExclusive();

            this.Close();

            // run build service
            var rebuilder = new RebuildService(_settings);

            long diff;
            try
            {
                // return how many bytes of diference from original/rebuild version
                diff = rebuilder.Rebuild(options, collation);
            }
            catch (Exception ex)
            {
                // SharedEngine retains this settings instance after disposing the
                // failed inner engine. Match it to a replacement left at the live path.
                if (ex.Data[RebuildService.LiveStateDataKey] as string == RebuildService.LiveStateReplacement)
                {
                    _settings.Password = password;
                    _settings.Collation = collation;
                }
                throw;
            }

            // SharedEngine retains this same settings instance for subsequent opens.
            _settings.Password = password;
            _settings.Collation = collation;

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
            return this.Rebuild(null);
        }

        /// <summary>
        /// Fill current database with data inside file reader - run inside a transacion
        /// </summary>
        internal void RebuildContent(IFileReader reader)
        {
            // begin transaction and get TransactionID
            var transaction = _monitor.GetTransaction(true, false, out _);

            try
            {
                foreach (var collection in reader.GetCollections())
                {
                    // get snapshot, indexer and data services
                    var snapshot = transaction.CreateSnapshot(LockMode.Write, collection, true);
                    var indexer = new IndexService(snapshot, _header.Pragmas.Collation, _disk.MAX_ITEMS_COUNT);
                    var data = new DataService(snapshot, _disk.MAX_ITEMS_COUNT);
                    var vectorService = new VectorIndexService(snapshot, _header.Pragmas.Collation);

                    // get all documents from current collection
                    var docs = reader.GetDocuments(collection);

                    // insert one-by-one
                    foreach (var doc in docs)
                    {
                        transaction.Safepoint();

                        this.InsertDocument(snapshot, doc, BsonAutoId.ObjectId, indexer, data, vectorService);
                    }

                    // first create all user indexes (exclude _id index)
                    foreach (var index in reader.GetIndexes(collection))
                    {
                        if (index.IndexType == 1 && index.VectorMetadata != null)
                        {
                            this.EnsureVectorIndex(
                                collection,
                                index.Name,
                                BsonExpression.Create(index.Expression),
                                new VectorIndexOptions(index.VectorMetadata.Dimensions, index.VectorMetadata.Metric));
                        }
                        else
                        {
                            this.EnsureIndex(
                                collection,
                                index.Name,
                                BsonExpression.Create(index.Expression),
                                index.Unique);
                        }
                    }
                }

                transaction.Commit();

                _monitor.ReleaseTransaction(transaction);
            }
            catch (Exception ex)
            {
                this.Close(ex);

                throw;
            }
        }
    }
}
