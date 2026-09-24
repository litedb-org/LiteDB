using System.Threading;
using System.Threading.Tasks;

namespace LiteDB.Demo.Tools.VectorSearch.Embedding
{
    internal interface IEmbeddingService
    {
        Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);
    }
}
