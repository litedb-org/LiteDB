namespace LiteDB
{
    public partial class LiteCollection<T>
    {
        /// <summary>
        /// Swaps this Collection to an other Parametric Type
        /// </summary>
        /// <typeparam name="K"></typeparam>
        /// <returns>New instance of the same Collection using another strong typed document definition</returns>
        public LiteCollection<K> Swap<K>()
        {
            return new LiteCollection<K>(_name,_engine,_mapper,_log);
        }

        /// <summary>
        /// Swaps this Collection to an other Parametric Type
        /// </summary>
        /// <returns>New instance of the same Collection using a generic BsonDocument</returns>
        public LiteCollection<BsonDocument> Swap()
        {
            return new LiteCollection<BsonDocument>(_name, _engine, _mapper, _log);
        }
    }
}
