# Vector search scores

Import `LiteDB.Vector` to return each vector search hit with its score:

```csharp
using System.Linq;
using LiteDB.Vector;

var hits = collection.Query()
    .TopKNearWithScore(x => x.Embedding, queryVector, k: 5)
    .Select(hit => new { hit.Document, hit.Score, hit.Metric })
    .ToList();

var nearby = collection.Query()
    .WhereNearWithScore(x => x.Embedding, queryVector, maxDistance: 0.3)
    .ToList();
```

`WhereNearWithScore`, `FindNearestWithScore`, and `TopKNearWithScore` accept a
mapped member expression, a stored field name, or a `BsonExpression`. Existing
`WhereNear`, `FindNearest`, and `TopKNear` APIs retain their return types and behavior.

For additional database filtering, projection, or paging, call `WithScore()` at
the end of an existing vector query:

```csharp
var hits = collection.Query()
    .Where(x => x.Enabled)
    .TopKNear(x => x.Embedding, queryVector, k: 5)
    .WithScore()
    .ToList();

var projected = collection.Query()
    .WhereNear(x => x.Embedding, queryVector, maxDistance: 0.3)
    .Select(x => new { x.Id, x.Title })
    .Limit(10)
    .WithScore()
    .ToList();
```

Each `VectorSearchResult<T>` has `Document`, `Score`, and `Metric` properties.
`Document` contains the original document or the database projection, with the
collection's BSON mapping applied. Metadata is kept outside the document, so
stored properties named `Score`, `Metric`, or `Document` are preserved.

| Metric | Score | Best match |
| --- | --- | --- |
| Cosine | `1 - cosine similarity` | Smallest (0 for identical directions) |
| Euclidean | Euclidean distance, including the square root | Smallest |
| DotProduct | Raw dot-product similarity | Largest |

The selected vector index determines the metric. For dot-product indexes,
`maxDistance` is a **minimum similarity**, as in the existing `WhereNear` API.
Top-K returns the best matches first. Threshold searches retain existing ordering:
index rank when indexed, ordinary query ordering otherwise.

When no compatible vector index is available, the query uses `VECTOR_SIM` and
reports `Metric = Cosine`. `Score` is nullable because cosine can be undefined
(for example, missing, mismatched, or zero-length/zero-magnitude vectors); those
hits retain the existing query's null semantics. To obtain cosine similarity
from a defined cosine distance, use `1 - hit.Score.Value`.

Indexed queries reuse the score computed by the vector search, including through
ordinary filtering, paging, and sorting. Unindexed queries compute the score for
each returned document in the engine projection, in addition to any calculation
needed for filtering or ordering. Vector indexes retain their approximate search
candidate budget; additional filters can yield fewer than K hits.

Scored methods return a lazy `IEnumerable<VectorSearchResult<T>>`. LINQ operations
after them run in the application. Enumerate while the database is open; stopping
enumeration early disposes the reader. Each enumeration executes the query again.
`WithScore` snapshots the terminal query options and leaves ordinary materialization
of the source query unchanged. It requires a `WhereNear` or `TopKNear` query and
rejects grouped or aggregate results, which have no single document score.
