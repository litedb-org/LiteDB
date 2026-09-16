namespace LiteDB
{
    public partial class LiteQueryable<T>
    {
        private readonly StreamReferenceMapper _streamReferenceMapper;

        private T Deserialize(BsonDocument document)
        {
            using (_mapper.UseStreamReferences(_streamReferenceMapper))
            {
                return (T)_mapper.Deserialize(typeof(T), document);
            }
        }
    }
}
