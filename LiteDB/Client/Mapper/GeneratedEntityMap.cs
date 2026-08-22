using System;

namespace LiteDB
{
    /// <summary>
    /// Provides statically authored document conversion for one source-generated entity type.
    /// </summary>
    /// <typeparam name="T">The exact entity type handled by this map.</typeparam>
    public sealed class GeneratedEntityMap<T>
    {
        private readonly Func<T, BsonDocument> _serialize;
        private readonly Func<BsonDocument, T> _deserialize;

        /// <summary>
        /// Initializes a generated execution map for <typeparamref name="T"/>.
        /// </summary>
        /// <param name="serialize">Creates a BSON document directly from a known entity instance.</param>
        /// <param name="deserialize">Creates a known entity instance directly from a BSON document.</param>
        public GeneratedEntityMap(Func<T, BsonDocument> serialize, Func<BsonDocument, T> deserialize)
        {
            _serialize = serialize ?? throw new ArgumentNullException(nameof(serialize));
            _deserialize = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
        }

        internal BsonDocument Serialize(T entity) => _serialize(entity);

        internal T Deserialize(BsonDocument document) => _deserialize(document);
    }
}
