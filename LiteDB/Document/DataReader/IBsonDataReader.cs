using System;
using System.Threading;
using System.Threading.Tasks;

namespace LiteDB
{
    public interface IBsonDataReader : IDisposable, IAsyncDisposable
    {
        BsonValue this[string field] { get; }

        string Collection { get; }
        BsonValue Current { get; }
        bool HasValues { get; }

        bool Read();

        ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default);
    }
}
