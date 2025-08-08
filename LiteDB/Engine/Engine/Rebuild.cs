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
        public long Rebuild(RebuildOptions options)
        {
            if (string.IsNullOrEmpty(_settings.Filename)) return 0;

            lock (_exclusiveRebuildGate)
            {
                var collation = _header?.Pragmas?.Collation ?? options?.Collation ?? Collation.Default;
                var password = options?.Password ?? _settings.Password;
                var effective = options ?? new RebuildOptions();
                if (effective.Collation == null) effective.Collation = collation;
                if (effective.Password == null) effective.Password = password;

                this.Close();

                var diff = new RebuildService(_settings).Rebuild(effective);

                this.Open();
                _state.Disposed = false;

                return diff;
            }
        }

        public long Rebuild()
        {
            var collation = new Collation(this.Pragma(Pragmas.COLLATION));
            var password = _settings.Password;

            return this.Rebuild(new RebuildOptions { Password = password, Collation = collation });
        }

        internal void RebuildContent(IFileReader reader)
        {
            var maxCount = GetSourceMaxItemsCount(_settings);
            RebuildContent(reader, maxCount);
        }

        private static uint GetSourceMaxItemsCount(EngineSettings settings)
        {
            var dataBytes = new FileInfo(settings.Filename).Length;
            var logFile = FileHelper.GetLogFile(settings.Filename);
            var logBytes = File.Exists(logFile) ? new FileInfo(logFile).Length : 0;
            return (uint)(((dataBytes + logBytes) / PAGE_SIZE + 10) * byte.MaxValue);
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

                    if (!RebuildHelpers.ValidatePkNoCycle(indexer, snapshot.CollectionPage.PK, out var pkCount, maxItemsCount))
                    {
                        throw new LiteException(0, $"Detected loop in PK index for collection '{collection}'.");
                    }

                    foreach (var idx in reader.GetIndexes(collection))
                    {
                        try
                        {
                            EnsureIndex(collection,
                                        idx.Name,
                                        BsonExpression.Create(idx.Expression),
                                        idx.Unique);
                        }
                        catch (LiteException ex) when (ex.Message.IndexOf("Detected loop in FindAll", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try { DropIndex(collection, idx.Name); } catch { /* best effort */ }

                            var expr = BsonExpression.Create(idx.Expression);

                            RebuildHelpers.EnsureIndexFromDataScan(
                                snapshot,
                                idx.Name,
                                expr,
                                idx.Unique,
                                indexer,
                                data,
                                transaction.Safepoint
                            );
                        }
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