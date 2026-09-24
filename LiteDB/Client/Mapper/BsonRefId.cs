using System;

namespace LiteDB;

/// <summary>
/// Helper to assign the ID of a DbRef property (see <see cref="BsonRefAttribute"/>) in expression trees.
/// </summary>
/// <typeparam name="T">The type of the referenced entity.</typeparam>
/// <example><code>
/// db.GetCollection&lt;A&gt;()
///   .UpdateMany(
///     x => new A
///     {
///       Id = x.Id,
///       Bref = new BsonRefId&lt;B&gt;(100),
///     },
///     x => x.Id == 11);
/// </code></example>
public sealed class BsonRefId<T>
{
    /// <summary>
    /// Assigns the ID of the referenced entity of type <typeparamref name="T"/>.
    /// </summary>
    /// <param name="id">The ID to assign.</param>
    public BsonRefId(BsonValue id)
    {
    }

    public static implicit operator T(BsonRefId<T> _)
    {
        throw new NotSupportedException("The type BsonRefId<T> can only be used in LiteDB LINQ expressions.");
    }
}
