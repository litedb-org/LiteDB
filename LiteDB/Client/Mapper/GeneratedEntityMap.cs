using System;

namespace LiteDB
{
    /// <summary>
    /// Type-erased view of a generated execution map, for callers that only hold a runtime instance.
    /// </summary>
    internal interface IGeneratedEntityMap
    {
        BsonDocument Serialize(object entity, GeneratedExecutionOptions options);
    }

    /// <summary>
    /// Provides statically authored document conversion for one source-generated entity type.
    /// </summary>
    /// <typeparam name="T">The exact entity type handled by this map.</typeparam>
    public sealed class GeneratedEntityMap<T> : IGeneratedEntityMap
    {
        private readonly Func<T, GeneratedExecutionOptions, BsonDocument> _serializeWithOptions;
        private readonly Func<BsonDocument, GeneratedExecutionOptions, T> _deserializeWithOptions;

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
        }

        /// <summary>
        /// Set for LiteDB's own models, whose maps fix every field name and therefore do not depend on naming
        /// conventions, member callbacks, or type registrations of the mapper they are registered with.
        /// </summary>
        internal bool IsConfigurationIndependent { get; set; }

        BsonDocument IGeneratedEntityMap.Serialize(object entity, GeneratedExecutionOptions options) =>
            this.Serialize((T)entity, options);

        internal BsonDocument Serialize(T entity, GeneratedExecutionOptions options) =>
            _serializeWithOptions(entity, options);

        internal T Deserialize(BsonDocument document, GeneratedExecutionOptions options) =>
            _deserializeWithOptions(document, options);
    }
}
