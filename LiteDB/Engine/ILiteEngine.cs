using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Engine
{
    public interface ILiteEngine : IDisposable, IAsyncDisposable
    {
        Task<int> CheckpointAsync(CancellationToken cancellationToken = default);
        Task<long> RebuildAsync(RebuildOptions options, CancellationToken cancellationToken = default);

        Task<bool> BeginTransAsync(CancellationToken cancellationToken = default);
        Task<bool> CommitAsync(CancellationToken cancellationToken = default);
        Task<bool> RollbackAsync(CancellationToken cancellationToken = default);

        Task<IBsonDataReader> QueryAsync(string collection, Query query, CancellationToken cancellationToken = default);

        Task<int> InsertAsync(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId, CancellationToken cancellationToken = default);
        Task<int> UpdateAsync(string collection, IEnumerable<BsonDocument> docs, CancellationToken cancellationToken = default);
        Task<int> UpdateManyAsync(string collection, BsonExpression transform, BsonExpression predicate, CancellationToken cancellationToken = default);
        Task<int> UpsertAsync(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId, CancellationToken cancellationToken = default);
        Task<int> DeleteAsync(string collection, IEnumerable<BsonValue> ids, CancellationToken cancellationToken = default);
        Task<int> DeleteManyAsync(string collection, BsonExpression predicate, CancellationToken cancellationToken = default);

        Task<bool> DropCollectionAsync(string name, CancellationToken cancellationToken = default);
        Task<bool> RenameCollectionAsync(string name, string newName, CancellationToken cancellationToken = default);

        Task<bool> EnsureIndexAsync(string collection, string name, BsonExpression expression, bool unique, CancellationToken cancellationToken = default);
        Task<bool> DropIndexAsync(string collection, string name, CancellationToken cancellationToken = default);

        Task<BsonValue> PragmaAsync(string name, CancellationToken cancellationToken = default);
        Task<bool> PragmaAsync(string name, BsonValue value, CancellationToken cancellationToken = default);
    }
}
