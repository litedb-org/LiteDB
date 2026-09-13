namespace LiteDB
{
    public partial class LiteRepository
    {
        /// <summary>
        /// Find an entity by id, or return the default value if it is missing.
        /// </summary>
        public T SingleOrDefaultById<T>(BsonValue id, string collectionName = null)
        {
            return this.Query<T>(collectionName).SingleOrDefaultById(id);
        }
    }
}
