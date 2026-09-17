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
        private readonly Func<T, GeneratedExecutionOptions, BsonDocument> _serializeWithOptions;
        private readonly Func<BsonDocument, GeneratedExecutionOptions, T> _deserializeWithOptions;

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

        /// <summary>
        /// Initializes an options-aware execution map emitted for a source-generated entity type.
        /// </summary>
        /// <param name="serialize">Creates a BSON document directly from a known entity instance and the supported mapper options.</param>
        /// <param name="deserialize">Creates a known entity instance directly from a BSON document and the supported mapper options.</param>
        public GeneratedEntityMap(
            Func<T, GeneratedExecutionOptions, BsonDocument> serialize,
            Func<BsonDocument, GeneratedExecutionOptions, T> deserialize)
        {
            _serializeWithOptions = serialize ?? throw new ArgumentNullException(nameof(serialize));
            _deserializeWithOptions = deserialize ?? throw new ArgumentNullException(nameof(deserialize));
            SupportsScalarOptions = true;
        }

        /// <summary>
        /// Gets whether this map supports the generated execution scalar option vector.
        /// </summary>
        public bool SupportsScalarOptions { get; }

        internal BsonDocument Serialize(T entity, GeneratedExecutionOptions options) =>
            _serializeWithOptions is null ? _serialize(entity) : _serializeWithOptions(entity, options);

        internal T Deserialize(BsonDocument document, GeneratedExecutionOptions options) =>
            _deserializeWithOptions is null ? _deserialize(document) : _deserializeWithOptions(document, options);
    }
}
